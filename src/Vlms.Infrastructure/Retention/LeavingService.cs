using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Retention;

/// <summary>
/// STATE.md "Manual 'leaving' flag for students and teachers, feeding the retention report" —
/// Philip's decision (2026-08-12), resolving the schema gap
/// <c>Reporting.RetentionFlagging</c>/<c>RecordsDueForDeletionService</c> found and documented
/// (commit 73eb0f8): leaving is recorded via a manual Admin-triggered action — a boolean the Admin
/// explicitly enables — not inferred from disengagement/activity, and not a bigger withdrawal
/// workflow.
///
/// Two entry points, one per entity kind named in docs/governance/security-compliance.md's
/// "Retention" section:
///
/// - <see cref="MarkStudentInactiveAsync"/> sets <see cref="Student.Status"/> to
///   <see cref="StudentStatus.Inactive"/> (the enum value already existed — see <c>Student.cs</c> —
///   but no write path anywhere in this codebase ever reached it before this) plus
///   <see cref="Student.LeftAt"/>.
/// - <see cref="MarkTeacherLeftAsync"/> sets the equivalent <see cref="AppUser.HasLeft"/>/
///   <see cref="AppUser.LeftAt"/> pair — see <see cref="AppUser.HasLeft"/>'s own doc comment for the
///   "generic field on AppUser, Teacher-scoped action" schema-placement judgement call.
///
/// <b>Auto-captured timestamp, not manually entered/backdated (a documented build-time reading,
/// flagged as revisitable):</b> STATE.md's item text asks for "a boolean the Admin explicitly
/// enables", not a date-entry form — so both methods below set the date fields to
/// <see cref="DateTime.UtcNow"/> at the moment the action runs, the same way
/// <c>Progress.CompletionService.ReverseAsync</c> captures <c>ReversedAt</c> automatically rather
/// than accepting a caller-supplied date. If Philip wants the Admin to be able to backdate a leaving
/// date (e.g. recording that someone left last month, only being told about it today), that is a
/// materially different UI/validation shape (a date picker, plus deciding whether the retention
/// clock should be allowed to run from a date earlier than when this record was created) and needs
/// its own deliberate decision, not a silent assumption here.
///
/// <b>Authorization: role-checked inside the service itself</b> (defense in depth, same pattern as
/// every other service in this codebase) — Admin-only for both methods, matching STATE.md's item
/// text exactly ("Admin-only, role-checked inside the service itself... no self-service path for a
/// Student/Teacher to mark themselves inactive/left"). This is narrower than
/// <c>Registration.StudentRegistrationService</c>'s Admin-or-Teacher gate — deliberately so, since
/// this action's whole point is a controlled, singular Admin decision, not a routine enrolment task
/// any Teacher can perform.
///
/// <b>No-cascade scope, explicitly (mirrors the certificate-visibility and completion-reversal
/// precedent — STATE.md, 2026-07-20/2026-07-22):</b> neither method here triggers deletion (that
/// stays the existing separate, manual Admin step in <c>Reporting.RecordsDueForDeletionService</c>
/// once a record is flagged as due) or touches anything else about the Student/Teacher — no
/// retroactive effect on promotions, badges, certificates, existing <see cref="DbsCheck"/> rows, or
/// any other entity. Setting the flag only ever changes the two fields named above.
/// </summary>
public sealed class LeavingService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;

    public LeavingService(VlmsDbContext db, ICurrentUserContext currentUser, UserManager<VlmsIdentityUser> userManager)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
    }

    /// <summary>
    /// Marks an Active <see cref="Student"/> as having left: sets <see cref="Student.Status"/> to
    /// <see cref="StudentStatus.Inactive"/> and <see cref="Student.LeftAt"/> to now. Admin-only.
    /// Throws if the student is not currently Active (an already-Inactive or Graduated student has
    /// no "leaving" transition to make — rejected with a clear message rather than silently
    /// overwriting an existing <see cref="Student.LeftAt"/> or reinterpreting a graduation as a
    /// leaving).
    /// </summary>
    public async Task<Student> MarkStudentInactiveAsync(int studentId, CancellationToken ct = default)
    {
        RequireAdmin();

        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);

        if (student.Status != StudentStatus.Active)
        {
            throw new InvalidOperationException(
                $"Student {studentId} is {student.Status}, not Active — only an Active student can be marked as having left.");
        }

        var now = DateTime.UtcNow;
        _db.Entry(student).CurrentValues.SetValues(new Student
        {
            Id = student.Id,
            Name = student.Name,
            DateOfBirth = student.DateOfBirth,
            CurrentRankId = student.CurrentRankId,
            Status = StudentStatus.Inactive,
            EnrolmentDate = student.EnrolmentDate,
            AssignedTeacherUserId = student.AssignedTeacherUserId,
            AppUserId = student.AppUserId,
            LeftAt = now
        });

        await _db.SaveChangesAsync(ct);
        return student;
    }

    /// <summary>
    /// Marks a Teacher (an <see cref="AppUser"/> holding <see cref="Role.Teacher"/>) as having left:
    /// sets <see cref="AppUser.HasLeft"/> to true and <see cref="AppUser.LeftAt"/> to now.
    /// Admin-only. Throws if <paramref name="teacherUserId"/> doesn't exist, doesn't hold
    /// <see cref="Role.Teacher"/> (same validation <c>Safeguarding.DbsCheckService.RecordAsync</c>
    /// already applies when recording a DBS check against a teacher), or has already been marked as
    /// having left.
    /// </summary>
    public async Task<AppUser> MarkTeacherLeftAsync(int teacherUserId, CancellationToken ct = default)
    {
        RequireAdmin();

        var teacher = await _db.AppUsers.SingleAsync(u => u.Id == teacherUserId, ct);

        var isTeacher = await _db.UserRoles.AnyAsync(r => r.UserId == teacherUserId && r.Role == Role.Teacher, ct);
        if (!isTeacher)
        {
            throw new InvalidOperationException(
                $"AppUser {teacherUserId} does not hold the Teacher role — only a Teacher can be marked as having left.");
        }

        if (teacher.HasLeft)
        {
            throw new InvalidOperationException($"AppUser {teacherUserId} has already been marked as having left.");
        }

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.Entry(teacher).CurrentValues.SetValues(new AppUser
        {
            Id = teacher.Id,
            DisplayName = teacher.DisplayName,
            Email = teacher.Email,
            HasLeft = true,
            LeftAt = now
        });

        await _db.SaveChangesAsync(ct);
        // FR-016: leaving changes what the user may do, so their existing sign-ins are re-validated.
        await SecurityStamps.RotateAsync(_userManager, teacherUserId);
        await transaction.CommitAsync(ct);
        return teacher;
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to mark a Student/Teacher as having left.");
        }
    }
}
