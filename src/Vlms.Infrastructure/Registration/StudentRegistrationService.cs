using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Guardianship;

namespace Vlms.Infrastructure.Registration;

/// <summary>
/// Student registration/enrolment (docs/design/data-design.md's <c>Student</c>/
/// <c>StudentRankProgress</c> entities, functional.md FR-004, STATE.md): in one operation, creates
/// the <see cref="Student"/> record, opens their first <see cref="StudentRankProgress"/> row, and
/// creates at least one <see cref="StudentGuardianLink"/> by calling into the existing
/// <see cref="GuardianLinkService"/> (not duplicating its logic) — matching data-design.md's
/// "Guardian link verification" wording that the Admin/Teacher enters the guardian's details at the
/// same time as the student's.
///
/// Atomicity: the Student/StudentRankProgress write and the guardian-link write are two separate
/// <c>SaveChangesAsync</c> calls against the same <see cref="VlmsDbContext"/> instance (this service
/// and the injected <see cref="GuardianLinkService"/> share one DI-scoped context — see
/// <c>Program.cs</c>'s <c>AddScoped</c> registrations). Both entry points below wrap both calls in a
/// single <c>Database.BeginTransactionAsync</c>/<c>CommitAsync</c>, so a failure in the guardian step
/// (blank guardian name, an unknown <c>parentGuardianId</c>) rolls back the Student/
/// StudentRankProgress rows too — no orphaned Student with no guardian link, and no risk of a
/// duplicate Student from a UI retry after a partial failure.
///
/// Starting rank: data-design.md documents <see cref="Rank.Order"/> as already fully describing
/// ladder position ("the next rank" for <see cref="Vlms.Infrastructure.Progress.PromotionService"/>
/// is the Rank with the smallest Order greater than the current one) but does not explicitly name
/// which Rank a brand-new student starts at. Build-time judgement call, consistent with that same
/// Order-based model: the starting rank is the Rank with the smallest <see cref="Rank.Order"/> — the
/// bottom of the ladder. Throws if no Rank reference data exists yet (same "reference data must be
/// populated separately" precondition as PromotionService's RankBadge lookup), rather than
/// fabricating one.
///
/// This closes the precondition <see cref="Vlms.Infrastructure.Progress.PromotionService"/>'s doc
/// comment flags: it expects an open (<c>CompletedAt == null</c>) <see cref="StudentRankProgress"/>
/// row to already exist for the student's current rank. The row this service opens has
/// <c>RankId</c> equal to the Student's <c>CurrentRankId</c> (the starting rank) and no
/// <c>CompletedAt</c> set, satisfying that lookup exactly.
///
/// Authorization: role-checked inside this service itself (defense in depth, same pattern as
/// <see cref="GuardianLinkService"/>/<see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/>)
/// — Admin or Teacher only. This is a hard constraint (CLAUDE.md Project Law: "the Student record
/// that anchors [a StudentGuardianLink]" is subject to the same "never parent self-service" rule as
/// the link itself) — a Student record is never created by anyone other than Admin/Teacher.
/// <see cref="GuardianLinkService"/>'s own entry points independently role-check too, so a caller
/// reaching either service directly is denied at both layers, not just one.
///
/// Two entry points, mirroring <see cref="GuardianLinkService"/>'s "existing guardian" vs "register a
/// new guardian" split (and <c>GuardianLinks.razor</c>'s form, which this service's page reuses the
/// same UI shape for):
/// - <see cref="RegisterStudentWithNewGuardianAsync"/> — the common case: no <see cref="ParentGuardian"/>
///   record exists yet, so one is created and linked in the same operation.
/// - <see cref="RegisterStudentWithExistingGuardianAsync"/> — links an already-known guardian (e.g.
///   enrolling a second child of a family already in the system).
/// </summary>
public sealed class StudentRegistrationService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly GuardianLinkService _guardianLinks;

    public StudentRegistrationService(VlmsDbContext db, ICurrentUserContext currentUser, GuardianLinkService guardianLinks)
    {
        _db = db;
        _currentUser = currentUser;
        _guardianLinks = guardianLinks;
    }

    /// <summary>
    /// Registers a new Student and, in the same operation, registers a brand-new
    /// <see cref="ParentGuardian"/> and links it. Admin/Teacher only.
    /// </summary>
    public async Task<(Student Student, StudentRankProgress Progress, ParentGuardian Guardian, StudentGuardianLink Link)>
        RegisterStudentWithNewGuardianAsync(
            string name, DateOnly dateOfBirth, DateOnly enrolmentDate, int? assignedTeacherUserId,
            string guardianName, string guardianContactInfo, bool guardianIsPrimary, CancellationToken ct = default)
    {
        RequireAdminOrTeacher();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Atomicity: Student+StudentRankProgress and the guardian link are two separate
        // SaveChangesAsync calls (the second inside GuardianLinkService, reused rather than
        // duplicated). Wrapped in one explicit transaction, shared via the same VlmsDbContext
        // instance GuardianLinkService is constructed over, so a failure in the guardian step
        // (blank name, unknown parentGuardianId) rolls back the Student/StudentRankProgress rows
        // too, instead of leaving an orphaned Student with no guardian link. `await using` disposes
        // (and thus rolls back, per IDbContextTransaction) without an explicit catch if we never
        // reach CommitAsync.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var (student, progress) = await CreateStudentAndOpenRankProgressAsync(
            name, dateOfBirth, enrolmentDate, assignedTeacherUserId, ct);

        var (guardian, link) = await _guardianLinks.RegisterGuardianAndLinkAsync(
            student.Id, guardianName, guardianContactInfo, guardianIsPrimary, ct);

        await transaction.CommitAsync(ct);

        return (student, progress, guardian, link);
    }

    /// <summary>
    /// Registers a new Student and links an existing <see cref="ParentGuardian"/> to them (e.g. a
    /// second child of a family already known to the programme). Admin/Teacher only.
    /// </summary>
    public async Task<(Student Student, StudentRankProgress Progress, StudentGuardianLink Link)>
        RegisterStudentWithExistingGuardianAsync(
            string name, DateOnly dateOfBirth, DateOnly enrolmentDate, int? assignedTeacherUserId,
            int parentGuardianId, CancellationToken ct = default)
    {
        RequireAdminOrTeacher();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Same atomicity reasoning as RegisterStudentWithNewGuardianAsync above: an unknown
        // parentGuardianId throws inside GuardianLinkService.CreateLinkAsync's SingleAsync lookup,
        // after the Student/StudentRankProgress rows already exist in this same transaction — the
        // rollback on Dispose (never reaching CommitAsync) undoes those too.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var (student, progress) = await CreateStudentAndOpenRankProgressAsync(
            name, dateOfBirth, enrolmentDate, assignedTeacherUserId, ct);

        var link = await _guardianLinks.CreateLinkAsync(student.Id, parentGuardianId, ct);

        await transaction.CommitAsync(ct);

        return (student, progress, link);
    }

    private async Task<(Student Student, StudentRankProgress Progress)> CreateStudentAndOpenRankProgressAsync(
        string name, DateOnly dateOfBirth, DateOnly enrolmentDate, int? assignedTeacherUserId, CancellationToken ct)
    {
        var startingRank = await _db.Ranks.OrderBy(r => r.Order).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "No Rank reference data configured — cannot register a student without a starting rank.");

        var student = new Student
        {
            Id = await NextId(_db.Students, ct),
            Name = name,
            DateOfBirth = dateOfBirth,
            CurrentRankId = startingRank.Id,
            Status = StudentStatus.Active,
            EnrolmentDate = enrolmentDate,
            AssignedTeacherUserId = assignedTeacherUserId
        };
        _db.Students.Add(student);

        var progress = new StudentRankProgress
        {
            Id = await NextId(_db.StudentRankProgresses, ct),
            StudentId = student.Id,
            RankId = startingRank.Id,
            StartedAt = enrolmentDate.ToDateTime(TimeOnly.MinValue),
            CompletedAt = null
        };
        _db.StudentRankProgresses.Add(progress);

        await _db.SaveChangesAsync(ct);
        return (student, progress);
    }

    private void RequireAdminOrTeacher()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or Teacher role to register a student.");
        }
    }

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<Student> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;

    private static async Task<int> NextId(DbSet<StudentRankProgress> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
