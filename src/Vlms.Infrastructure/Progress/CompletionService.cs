using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Engagement;

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
/// Then, when registered, <see cref="GuardianNotificationService.NotifyCompletionAsync"/> emails the
/// student's guardians (BR-005); it runs after every save and never throws, so a mail failure can't
/// undo or fail the completion.
///
/// Not wrapped in a single explicit database transaction across all three steps: each service
/// call SaveChanges()s its own work. Kept simple per VISION.md's "fewest moving parts" principle
/// for a solo-maintained, tens-of-users system — a failure partway through (e.g. a blob upload
/// failing) leaves the completion recorded but the certificate/promotion step incomplete, which is
/// recoverable by re-running the flow rather than something that corrupts state. A documented
/// simplification, not an oversight.
///
/// <see cref="ReverseAsync"/> is the Teacher self-correction write path (functional.md: "can
/// self-correct/reverse their own entry ... no separate correction gate through Admin") — see its
/// own doc comment for the scope decisions made building it (own-entry-only scoping, the
/// <see cref="DefaultReversalWindowDays"/> time window, the optional-note stance, and why it does
/// not cascade to a past promotion). Until it was built, <see cref="StudentLessonCompletion.IsReversed"/>/
/// <see cref="StudentLessonCompletion.ReversedAt"/> existed on the entity and were read by
/// <see cref="PromotionService"/>/<c>ProgressReportingService</c>/<c>AtRiskStudentFlagging</c>/
/// <c>ParentDashboardService</c>, but nothing in <c>src/</c> ever set them — only test seed data did.
/// </summary>
public sealed class CompletionService
{
    /// <summary>
    /// functional.md names reversal as available "e.g. within some reasonable window" but gives no
    /// figure — the same "configurable, no number given" situation
    /// <see cref="Safeguarding.ConsentExpiryJob.DefaultExpiryWarningWindowDays"/> and
    /// <see cref="Notifications.NotificationService.DefaultSafeguardingCriticalMaxAttempts"/> already
    /// resolved for this codebase. A documented build-time default, not a silently-picked number:
    /// 7 days — long enough to cover "the Teacher (or a colleague, since Teachers see all students —
    /// see low-level-design.md's authorization model) noticed the mistake at the next session", short
    /// enough that this stays genuine self-correction of a fresh mistake rather than a general-purpose
    /// undo button that could quietly rewrite the audit trail functional.md's own "Progress tracking"
    /// section says certificates/badges must give ("who received what and when") long after the fact.
    /// A limit is deliberately enforced, not left open-ended — functional.md's wording implies a bound
    /// exists even though it doesn't name one. Configurable via the constructor, same pattern as the
    /// two services named above.
    /// </summary>
    public const int DefaultReversalWindowDays = 7;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly PromotionService _promotionService;
    private readonly CertificateService _certificateService;
    private readonly int _reversalWindowDays;
    private readonly GuardianNotificationService? _guardianNotifications;

    public CompletionService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        PromotionService promotionService,
        CertificateService certificateService,
        int reversalWindowDays = DefaultReversalWindowDays,
        GuardianNotificationService? guardianNotifications = null)
    {
        if (reversalWindowDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(reversalWindowDays), "Reversal window must be at least 1 day.");
        }

        _db = db;
        _currentUser = currentUser;
        _promotionService = promotionService;
        _certificateService = certificateService;
        _reversalWindowDays = reversalWindowDays;
        _guardianNotifications = guardianNotifications;
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

        var promoted = await _promotionService.CheckAndPromoteAsync(studentId, ct);
        await _certificateService.GenerateAsync(completion.Id, ct);

        if (_guardianNotifications is not null)
        {
            await _guardianNotifications.NotifyCompletionAsync(completion.Id, promoted, ct);
        }

        return completion;
    }

    /// <summary>
    /// Teacher self-correction (functional.md: "can self-correct/reverse their own entry (e.g.
    /// within some reasonable window) — no separate correction gate through Admin"). Sets
    /// <see cref="StudentLessonCompletion.IsReversed"/>/<see cref="StudentLessonCompletion.ReversedAt"/>
    /// on an already-recorded completion — the missing write path this item was built to close.
    ///
    /// Scope decisions made explicitly here, per this item's own instruction not to leave any of
    /// them implicit:
    ///
    /// 1. **Own entry only, not any Teacher's.** functional.md says "their own entry" — a materially
    ///    narrower rule than low-level-design.md's "a Teacher sees all students in the programme...
    ///    not scoped to AssignedTeacherUserId", which is about which *students* a Teacher may act on,
    ///    not who may undo a specific already-made completion. Only the Teacher recorded in
    ///    <see cref="StudentLessonCompletion.CompletedByUserId"/> may reverse it; any other Teacher is
    ///    denied (<see cref="UnauthorizedAccessException"/>), matching this codebase's "no mutation on
    ///    denial" convention.
    /// 2. **A bounded time window is enforced, not left open-ended** — see
    ///    <see cref="DefaultReversalWindowDays"/>'s own doc comment for the full reasoning.
    /// 3. **The optional-note stance carries over from completion, not tightened.** functional.md
    ///    frames the evidence/note field as "available but optional (low friction, not a mandatory
    ///    audit gate)" and wants "no separate correction gate through Admin" for reversal — a
    ///    mandatory reversal reason would itself be a small mandatory gate on the exact self-service
    ///    flow that sentence rejects. There is one <see cref="StudentLessonCompletion.Note"/> field
    ///    per completion, not a separate reversal-note column (data-design.md names only one) — a
    ///    supplied reversal note is appended to whatever the completion's existing Note already said,
    ///    as a clearly delimited "Reversed: {reason}" line, rather than overwriting it.
    /// 4. **Does not cascade to a past promotion.** <see cref="PromotionService.CheckAndPromoteAsync"/>
    ///    already excludes reversed completions from *future* rank-progression counting (unchanged).
    ///    Whether reversing a completion that already contributed to a promotion that already happened
    ///    should retroactively un-promote the student (and, transitively, un-award any badge and
    ///    undo any further progress made since) is a separate, materially larger question that neither
    ///    data-design.md nor low-level-design.md asks for — explicitly out of scope here, the same
    ///    narrowly-scoped-effect precedent the certificate-visibility decision (STATE.md, 2026-07-20)
    ///    already set for this exact field. If ever required, it needs its own deliberate decision and
    ///    item, not a silent side effect of this one.
    ///
    /// Rejects an unknown <paramref name="completionId"/> via <c>SingleAsync</c> (this codebase's
    /// existing convention) and rejects reversing an already-reversed completion with a clear
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<StudentLessonCompletion> ReverseAsync(int completionId, string? note = null, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);
        var teacherUserId = RequireResolvedUserId();

        var completion = await _db.StudentLessonCompletions.SingleAsync(c => c.Id == completionId, ct);

        if (completion.CompletedByUserId != teacherUserId)
        {
            throw new UnauthorizedAccessException(
                $"Completion {completionId} was recorded by a different Teacher — only the recording Teacher may reverse their own entry (functional.md).");
        }

        if (completion.IsReversed)
        {
            throw new InvalidOperationException($"Completion {completionId} has already been reversed.");
        }

        var now = DateTime.UtcNow;
        var deadline = completion.CompletedAt.AddDays(_reversalWindowDays);
        if (now > deadline)
        {
            throw new InvalidOperationException(
                $"Completion {completionId} was recorded on {completion.CompletedAt:u}, more than {_reversalWindowDays} day(s) ago — the self-correction window has passed.");
        }

        var mergedNote = string.IsNullOrWhiteSpace(note)
            ? completion.Note
            : string.IsNullOrWhiteSpace(completion.Note)
                ? $"Reversed: {note}"
                : $"{completion.Note}\nReversed: {note}";

        _db.Entry(completion).CurrentValues.SetValues(new StudentLessonCompletion
        {
            Id = completion.Id,
            StudentId = completion.StudentId,
            LessonId = completion.LessonId,
            CompletedByUserId = completion.CompletedByUserId,
            CompletedAt = completion.CompletedAt,
            Note = mergedNote,
            IsReversed = true,
            ReversedAt = now
        });

        await _db.SaveChangesAsync(ct);
        return completion;
    }

    /// <summary>
    /// Records one lesson completion for each of the given students (functional.md FR-008): the
    /// Teacher ticks the students who completed a lesson and submits once. Each student is
    /// processed independently via <see cref="MarkCompleteAsync"/> — one student's failure (lapsed
    /// consent, an unexpected error) never stops the others, and every student gets a result.
    /// A student who already has a non-reversed completion of the lesson is reported as
    /// <see cref="CompletionOutcome.AlreadyCompleted"/> and no second row is written.
    /// The whole call is refused (<see cref="UnauthorizedAccessException"/>) for a non-Teacher,
    /// and (<see cref="InvalidOperationException"/>) if the lesson does not exist or is retired.
    /// </summary>
    public async Task<IReadOnlyList<StudentCompletionResult>> MarkCompleteBatchAsync(
        IEnumerable<int> studentIds, int lessonId, string? note = null, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);
        _ = RequireResolvedUserId();

        var lesson = await _db.Lessons.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lessonId, ct)
            ?? throw new InvalidOperationException($"Lesson {lessonId} does not exist.");
        if (!lesson.IsActive)
        {
            throw new InvalidOperationException($"Lesson {lessonId} is retired — completions can only be recorded for active lessons.");
        }

        var results = new List<StudentCompletionResult>();
        foreach (var studentId in studentIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await MarkOneAsync(studentId, lessonId, note, ct));
        }

        return results;
    }

    private async Task<StudentCompletionResult> MarkOneAsync(int studentId, int lessonId, string? note, CancellationToken ct)
    {
        try
        {
            var alreadyDone = await _db.StudentLessonCompletions.AnyAsync(
                c => c.StudentId == studentId && c.LessonId == lessonId && !c.IsReversed, ct);
            if (alreadyDone)
            {
                return new StudentCompletionResult(studentId, CompletionOutcome.AlreadyCompleted, false,
                    "Already completed this lesson.");
            }

            var before = await _db.Students.AsNoTracking()
                .Where(s => s.Id == studentId)
                .Select(s => new { s.CurrentRankId, s.Status })
                .SingleAsync(ct);

            await MarkCompleteAsync(studentId, lessonId, note, ct);

            var after = await _db.Students.AsNoTracking()
                .Where(s => s.Id == studentId)
                .Select(s => new { s.CurrentRankId, s.Status })
                .SingleAsync(ct);
            var promoted = after.CurrentRankId != before.CurrentRankId || after.Status != before.Status;

            return new StudentCompletionResult(studentId, CompletionOutcome.Completed, promoted, null);
        }
        catch (ConsentBlockedException ex)
        {
            _db.ChangeTracker.Clear();
            return new StudentCompletionResult(studentId, CompletionOutcome.ConsentBlocked, false, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Isolate the failure: discard anything half-tracked so the next student starts clean.
            _db.ChangeTracker.Clear();
            return new StudentCompletionResult(studentId, CompletionOutcome.Failed, false, ex.Message);
        }
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
            throw new ConsentBlockedException(
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

/// <summary>Per-student outcome of <see cref="CompletionService.MarkCompleteBatchAsync"/>.</summary>
public enum CompletionOutcome
{
    Completed,
    AlreadyCompleted,
    ConsentBlocked,
    Failed
}

/// <summary>
/// Result for one student in a batch. <see cref="Promoted"/> is true when recording the completion
/// moved the student up a rank or graduated them. <see cref="Message"/> carries the reason for any
/// outcome other than <see cref="CompletionOutcome.Completed"/>.
/// </summary>
public sealed record StudentCompletionResult(int StudentId, CompletionOutcome Outcome, bool Promoted, string? Message);

/// <summary>
/// Thrown when lesson completion is blocked by missing/expired consent (FR-003). A subclass of
/// <see cref="InvalidOperationException"/> so existing callers that catch that keep working, while
/// the batch path can report the block distinctly from other errors.
/// </summary>
public sealed class ConsentBlockedException(string message) : InvalidOperationException(message);
