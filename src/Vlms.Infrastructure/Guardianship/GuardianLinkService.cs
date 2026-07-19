using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Guardianship;

/// <summary>
/// Guardian-link creation (docs/design/low-level-design.md "Authorization model",
/// docs/design/data-design.md "Guardian link verification", docs/requirements/functional.md
/// FR-004, STATE.md): creates the <see cref="StudentGuardianLink"/> join row that a Parent's
/// resource-based access to a <see cref="Student"/>
/// (<see cref="Vlms.Infrastructure.Authorization.ParentStudentAccessHandler"/>) depends on.
///
/// Authorization: role-checked inside the service itself (defense in depth, same pattern as
/// <see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/>/
/// <see cref="Vlms.Infrastructure.Progress.CompletionService"/>) — restricted to Admin or Teacher.
/// This is a hard constraint (CLAUDE.md Project Law; functional.md FR-004; data-design.md
/// "Guardian link verification"): a <see cref="StudentGuardianLink"/> must never be created by
/// parent self-service. There is deliberately no code path anywhere in this codebase — service
/// method, page, or otherwise — that lets a Parent create their own link; both entry points below
/// (<see cref="CreateLinkAsync"/>/<see cref="RegisterGuardianAndLinkAsync"/>) role-check Admin/
/// Teacher before doing anything else, so a caller reaching this service by any path other than
/// the intended UI is still denied here, not just kept out by page-level policy gating.
///
/// Two entry points, matching data-design.md's "the Admin/Teacher enters the guardian's details"
/// wording:
/// - <see cref="RegisterGuardianAndLinkAsync"/> — the common case at a new student's registration:
///   the guardian has no <see cref="ParentGuardian"/> record yet, so this creates one and links it
///   in a single call.
/// - <see cref="CreateLinkAsync"/> — links an *existing* ParentGuardian (e.g. the same parent's
///   second child) without creating a duplicate ParentGuardian row.
///
/// Duplicate-link handling: <see cref="StudentGuardianLink"/>'s composite primary key
/// (StudentId, ParentGuardianId — see VlmsDbContext.OnModelCreating) already makes a second
/// identical link impossible at the database level; this service checks first and throws a clear
/// <see cref="InvalidOperationException"/> instead of surfacing a raw DbUpdateException/unique-
/// constraint violation to the caller.
///
/// Scope note (deliberate, per the STATE.md item's explicit wording): this service and its page
/// implement only the guardian-link creation flow FR-004 asks for — not Student or full
/// ParentGuardian CRUD/registration. In particular it does NOT open a Student's first
/// StudentRankProgress row (the precondition PromotionService's doc comment flags as expected to
/// land with "student registration"); that belongs to a not-yet-built student-registration/
/// enrolment flow, added as a new STATE.md Next item by this change.
/// </summary>
public sealed class GuardianLinkService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public GuardianLinkService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Links an existing <see cref="ParentGuardian"/> to a <see cref="Student"/>. Admin/Teacher
    /// only. Throws if either record doesn't exist, or if the link already exists.
    /// </summary>
    public async Task<StudentGuardianLink> CreateLinkAsync(
        int studentId, int parentGuardianId, CancellationToken ct = default)
    {
        RequireAdminOrTeacher();
        var createdByUserId = RequireResolvedUserId();

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        _ = await _db.ParentGuardians.SingleAsync(g => g.Id == parentGuardianId, ct);

        await RequireNoExistingLinkAsync(studentId, parentGuardianId, ct);

        var link = new StudentGuardianLink
        {
            StudentId = studentId,
            ParentGuardianId = parentGuardianId,
            CreatedByUserId = createdByUserId
        };

        _db.StudentGuardianLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>
    /// Creates a new <see cref="ParentGuardian"/> from the details entered by the Admin/Teacher,
    /// then links it to the Student — the common path at a new student's registration, where no
    /// ParentGuardian record exists yet. Admin/Teacher only.
    /// </summary>
    public async Task<(ParentGuardian Guardian, StudentGuardianLink Link)> RegisterGuardianAndLinkAsync(
        int studentId, string guardianName, string contactInfo, bool isPrimary, CancellationToken ct = default)
    {
        RequireAdminOrTeacher();
        var createdByUserId = RequireResolvedUserId();
        ArgumentException.ThrowIfNullOrWhiteSpace(guardianName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactInfo);

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);

        var guardian = new ParentGuardian
        {
            Id = await NextId(_db.ParentGuardians, ct),
            Name = guardianName,
            ContactInfo = contactInfo,
            IsPrimary = isPrimary
        };
        _db.ParentGuardians.Add(guardian);

        var link = new StudentGuardianLink
        {
            StudentId = studentId,
            ParentGuardianId = guardian.Id,
            CreatedByUserId = createdByUserId
        };
        _db.StudentGuardianLinks.Add(link);

        await _db.SaveChangesAsync(ct);
        return (guardian, link);
    }

    /// <summary>data-design.md's StudentGuardianLink composite key already prevents a duplicate row
    /// at the database level; this checks first so the caller sees a clear message rather than a
    /// raw unique-constraint violation.</summary>
    private async Task RequireNoExistingLinkAsync(int studentId, int parentGuardianId, CancellationToken ct)
    {
        var exists = await _db.StudentGuardianLinks.AnyAsync(
            l => l.StudentId == studentId && l.ParentGuardianId == parentGuardianId, ct);

        if (exists)
        {
            throw new InvalidOperationException(
                $"Student {studentId} is already linked to guardian {parentGuardianId}.");
        }
    }

    private void RequireAdminOrTeacher()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or Teacher role to create a guardian link.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<ParentGuardian> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
