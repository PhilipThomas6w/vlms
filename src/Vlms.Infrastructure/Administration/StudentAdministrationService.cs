using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Retention;

namespace Vlms.Infrastructure.Administration;

/// <summary>
/// functional.md FR-013 (Student details) — "Only Admins assign a student's teacher and change a
/// student's status" (user-journeys.md J6 step 3). Name and date of birth are deliberately not
/// editable here: they come from the parent's consent form (a separate item).
///
/// <b>Authorization: role-checked inside the service itself</b> (defense in depth, same pattern as
/// <see cref="Identity.RoleAssignmentService"/> and <see cref="LeavingService"/>) — Admin-only for
/// every method, including the reads.
///
/// <b>Teacher assignment:</b> <see cref="AssignTeacherAsync"/> sets or clears
/// <see cref="Student.AssignedTeacherUserId"/>. The assignee must hold <see cref="Role.Teacher"/> and
/// must not be marked as having left (<see cref="AppUser.HasLeft"/>); clearing (null) is always
/// allowed. The field stays informational (Teachers see all students).
///
/// <b>Status rules (<see cref="ChangeStatusAsync"/>), consistent with the leaving/retention design:</b>
/// <list type="bullet">
/// <item>Active to Inactive: delegates to <see cref="LeavingService.MarkStudentInactiveAsync"/>, so
/// <see cref="Student.LeftAt"/> is captured identically to the "mark as left" flow and
/// <c>Reporting.RetentionFlagging</c> picks the student up from that date. No logic is
/// duplicated.</item>
/// <item>Inactive to Active (reactivation): allowed, and clears <see cref="Student.LeftAt"/>. A
/// student who returns has not left, so the 3-year retention clock must stop; leaving the old date
/// in place would flag a current student as due for deletion. If they leave again,
/// <see cref="LeavingService"/> records a fresh date.</item>
/// <item>Anything to Graduated: refused. Graduation is set only by <c>Progress.PromotionService</c>,
/// in the same operation that closes the final <see cref="StudentRankProgress"/> row, which is the
/// retention anchor for graduates. Setting it by hand would leave no anchor and the student would be
/// silently skipped by the retention report.</item>
/// <item>Graduated to anything: refused, because reopening a graduation would need that closed
/// rank-progress row reopened too, which is out of scope.</item>
/// <item>Requesting the status a student already has is refused rather than silently succeeding.</item>
/// </list>
/// </summary>
public sealed class StudentAdministrationService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly LeavingService _leavingService;

    public StudentAdministrationService(VlmsDbContext db, ICurrentUserContext currentUser, LeavingService leavingService)
    {
        _db = db;
        _currentUser = currentUser;
        _leavingService = leavingService;
    }

    /// <summary>One student row for the administration page.</summary>
    public sealed record StudentSummary(
        int StudentId, string Name, string RankName, StudentStatus Status,
        int? AssignedTeacherUserId, string? AssignedTeacherName);

    /// <summary>A user who can be assigned as a student's teacher.</summary>
    public sealed record AssignableTeacher(int UserId, string DisplayName);

    /// <summary>Every student with rank, status and assigned teacher, ordered by name. Admin-only.</summary>
    public async Task<IReadOnlyList<StudentSummary>> ListStudentsAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        return await _db.Students
            .OrderBy(s => s.Name)
            .Select(s => new StudentSummary(
                s.Id,
                s.Name,
                s.CurrentRank!.Name,
                s.Status,
                s.AssignedTeacherUserId,
                s.AssignedTeacherUser != null ? s.AssignedTeacherUser.DisplayName : null))
            .ToListAsync(ct);
    }

    /// <summary>Teachers who have not left, ordered by display name. Admin-only.</summary>
    public async Task<IReadOnlyList<AssignableTeacher>> ListAssignableTeachersAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        // This chapter's teachers only (FR-021, docs/adr/0010-chapters.md).
        var teacherIds = await _db.UserRoles
            .Where(r => r.Role == Role.Teacher && r.ChapterId == _currentUser.ActiveChapterId)
            .Select(r => r.UserId)
            .ToListAsync(ct);

        var chapterId = _currentUser.ActiveChapterId;
        return await _db.AppUsers
            .Where(u => teacherIds.Contains(u.Id) && !_db.StaffStepDowns.Any(s => s.UserId == u.Id && s.ChapterId == chapterId && s.ReinstatedAt == null))
            .OrderBy(u => u.DisplayName)
            .Select(u => new AssignableTeacher(u.Id, u.DisplayName))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Sets <see cref="Student.AssignedTeacherUserId"/> to <paramref name="teacherUserId"/>, or
    /// clears it when null. Throws if the user doesn't exist, doesn't hold <see cref="Role.Teacher"/>,
    /// or has left. Admin-only.
    /// </summary>
    public async Task<Student> AssignTeacherAsync(int studentId, int? teacherUserId, CancellationToken ct = default)
    {
        RequireAdmin();

        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);

        if (teacherUserId is { } teacherId)
        {
            var teacher = await _db.AppUsers.SingleOrDefaultAsync(u => u.Id == teacherId, ct)
                ?? throw new InvalidOperationException($"AppUser {teacherId} does not exist.");

            var isTeacher = await _db.UserRoles.AnyAsync(
                r => r.UserId == teacherId && r.Role == Role.Teacher && r.ChapterId == student.ChapterId, ct);
            if (!isTeacher)
            {
                throw new InvalidOperationException(
                    $"AppUser {teacherId} does not hold the Teacher role — only a Teacher can be assigned to a student.");
            }

            if (await _db.StaffStepDowns.AnyAsync(s => s.UserId == teacherId && s.ChapterId == student.ChapterId && s.ReinstatedAt == null, ct))
            {
                throw new InvalidOperationException(
                    $"AppUser {teacherId} is Inactive in this chapter and cannot be assigned to a student.");
            }
        }

        _db.Entry(student).CurrentValues.SetValues(Copy(student, student.Status, student.LeftAt, teacherUserId));
        await _db.SaveChangesAsync(ct);
        return student;
    }

    /// <summary>
    /// Changes <see cref="Student.Status"/> under the rules in this class's doc comment. Admin-only.
    /// </summary>
    public async Task<Student> ChangeStatusAsync(int studentId, StudentStatus newStatus, CancellationToken ct = default)
    {
        RequireAdmin();

        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);

        if (student.Status == newStatus)
        {
            throw new InvalidOperationException($"Student {studentId} is already {newStatus}.");
        }

        switch (student.Status, newStatus)
        {
            case (StudentStatus.Active, StudentStatus.Inactive):
                return await _leavingService.MarkStudentInactiveAsync(studentId, ct);

            case (StudentStatus.Inactive, StudentStatus.Active):
                _db.Entry(student).CurrentValues.SetValues(
                    Copy(student, StudentStatus.Active, leftAt: null, student.AssignedTeacherUserId));
                await _db.SaveChangesAsync(ct);
                return student;

            case (_, StudentStatus.Graduated):
                throw new InvalidOperationException(
                    "Graduated is set only by a promotion through the final rank; it cannot be set manually.");

            default:
                throw new InvalidOperationException(
                    $"Student {studentId} is {student.Status}; a graduated student's status cannot be changed.");
        }
    }

    private static Student Copy(Student student, StudentStatus status, DateTime? leftAt, int? assignedTeacherUserId) => new()
    {
        Id = student.Id,
        Name = student.Name,
        DateOfBirth = student.DateOfBirth,
        CurrentRankId = student.CurrentRankId,
        Status = status,
        EnrolmentDate = student.EnrolmentDate,
        AssignedTeacherUserId = assignedTeacherUserId,
        AppUserId = student.AppUserId,
        LeftAt = leftAt
    };

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to assign a teacher or change a student's status.");
        }
    }
}
