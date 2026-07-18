using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Progress;

/// <summary>
/// Progress tracking (docs/design/low-level-design.md "CompletionService", STATE.md): a Teacher
/// marks a <see cref="Lesson"/> complete for a <see cref="Student"/>.
///
/// Authorization: role-checked inside the service itself (defense in depth), same reasoning and
/// pattern as <see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/> — not solely
/// reliant on page-level policy gating.
///
/// Hard business rule (VISION.md "Safeguarding data..." / functional.md FR-003): expired or
/// missing consent blocks lesson completion. This reads only the non-sensitive
/// <see cref="ConsentRecord"/> (Status/ExpiryDate) — never <see cref="ConsentSensitiveDetails"/>,
/// which this service has no business touching (the Approver/Teacher-facing completion flow is
/// not a safeguarding-data access point).
///
/// On success, triggers <see cref="PromotionService"/>'s auto-promotion check and then
/// <see cref="CertificateService.GenerateAsync"/> (data-design.md: a <see cref="Certificate"/> is
/// generated per completion, not just per promotion) — matching the order named in
/// low-level-design.md ("triggers PromotionService check and CertificateService.Generate(...)").
///
/// Not wrapped in a single explicit database transaction across all three steps: each service
/// call SaveChanges()s its own work. Kept simple per VISION.md's "fewest moving parts" principle
/// for a solo-maintained, tens-of-users system — a failure partway through (e.g. a blob upload
/// failing) leaves the completion recorded but the certificate/promotion step incomplete, which is
/// recoverable by re-running the flow rather than something that corrupts state. A documented
/// simplification, not an oversight.
/// </summary>
public sealed class CompletionService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly PromotionService _promotionService;
    private readonly CertificateService _certificateService;

    public CompletionService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        PromotionService promotionService,
        CertificateService certificateService)
    {
        _db = db;
        _currentUser = currentUser;
        _promotionService = promotionService;
        _certificateService = certificateService;
    }

    /// <summary>
    /// Records a <see cref="StudentLessonCompletion"/> for the given student/lesson, then triggers
    /// the auto-promotion check and certificate generation. Blocked (throws
    /// <see cref="InvalidOperationException"/>) if the student has no currently-approved,
    /// unexpired <see cref="ConsentRecord"/>.
    /// </summary>
    public async Task<StudentLessonCompletion> MarkCompleteAsync(
        int studentId, int lessonId, string? note = null, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);
        var teacherUserId = RequireResolvedUserId();

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        _ = await _db.Lessons.SingleAsync(l => l.Id == lessonId, ct);

        await RequireActiveConsentAsync(studentId, ct);

        var completion = new StudentLessonCompletion
        {
            Id = await NextId(_db.StudentLessonCompletions, ct),
            StudentId = studentId,
            LessonId = lessonId,
            CompletedByUserId = teacherUserId,
            CompletedAt = DateTime.UtcNow,
            Note = note,
            IsReversed = false
        };

        _db.StudentLessonCompletions.Add(completion);
        await _db.SaveChangesAsync(ct);

        await _promotionService.CheckAndPromoteAsync(studentId, ct);
        await _certificateService.GenerateAsync(completion.Id, ct);

        return completion;
    }

    /// <summary>
    /// data-design.md: "Expiry blocks StudentLessonCompletion creation for that student." A student
    /// with no ConsentRecord at all, or only expired/non-Approved ones, is blocked.
    /// </summary>
    private async Task RequireActiveConsentAsync(int studentId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var hasActiveConsent = await _db.ConsentRecords.AnyAsync(
            c => c.StudentId == studentId && c.Status == ConsentStatus.Approved && c.ExpiryDate >= today, ct);

        if (!hasActiveConsent)
        {
            throw new InvalidOperationException(
                $"Student {studentId} has no active (approved, unexpired) consent record — lesson completion is blocked (functional.md FR-003).");
        }
    }

    private void RequireRole(Role role)
    {
        if (!_currentUser.HasRole(role))
        {
            throw new UnauthorizedAccessException($"Caller does not hold the {role} role.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<StudentLessonCompletion> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
