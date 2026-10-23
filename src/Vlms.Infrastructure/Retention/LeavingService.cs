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
/// - <see cref="MarkStaffLeftAsync"/> sets the equivalent <see cref="AppUser.HasLeft"/>/
///   <see cref="AppUser.LeftAt"/> pair for a Teacher, Admin or Safeguarding Officer (Teacher only
///   until FR-017 was amended on 22/10/2026) and ends their staff access — see <see cref="AppUser.HasLeft"/>'s
///   own doc comment for the schema-placement judgement call.
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
/// any other entity, and no role rows removed. Setting the flag only ever changes the two fields
/// named above, plus, for staff, a rotated security stamp (FR-016/FR-017).
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
    /// Marks a staff member (an <see cref="AppUser"/> holding <see cref="Role.Teacher"/>,
    /// <see cref="Role.Admin"/> or <see cref="Role.SafeguardingOfficer"/>) as having left: sets
    /// <see cref="AppUser.HasLeft"/> to true and <see cref="AppUser.LeftAt"/> to now. Admin-only.
    ///
    /// <b>Takes away their staff access at once</b> (functional.md FR-017, Philip 22/10/2026,
    /// clarified 23/10/2026; <see cref="LeftStaffPolicy"/>): from this moment their staff roles count
    /// as not held (<c>Security.IdentityCurrentUserContext</c>), and the security stamp is rotated in
    /// the same transaction so open sessions are re-validated. With no other role they can't sign in
    /// at all; a staff member who is also a Parent keeps parent access to their own children.
    ///
    /// <b>Role rows are kept, not revoked.</b> They are the history (the retention report, DBS checks
    /// against a teacher, who held Admin) and what marks this account as a staff leaver rather than a
    /// parent who left; <see cref="RoleAssignmentService.RevokeAsync"/> refuses to remove them.
    ///
    /// Throws if <paramref name="userId"/> doesn't exist, holds none of the three staff roles, has
    /// already been marked as having left, is the caller (an Admin can't lock themselves out), or is
    /// the last Admin who hasn't left (the system would be left with no Admin).
    /// </summary>
    public async Task<AppUser> MarkStaffLeftAsync(int userId, CancellationToken ct = default)
    {
        RequireAdmin();

        if (_currentUser.UserId == userId)
        {
            throw new InvalidOperationException(
                "You can't mark yourself as having left: it would lock you out. Ask another Admin to do it.");
        }

        var user = await _db.AppUsers.SingleAsync(u => u.Id == userId, ct);

        var roles = await _db.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role).ToListAsync(ct);
        if (!roles.Any(LeftStaffPolicy.IsStaffRole))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is not a Teacher, Admin or Safeguarding Officer — only staff can be marked as having left here.");
        }

        if (user.HasLeft)
        {
            throw new InvalidOperationException($"{user.DisplayName} has already been marked as having left.");
        }

        if (roles.Contains(Role.Admin)
            && !await _db.AppUsers.AnyAsync(u => u.Id != userId && !u.HasLeft && u.Roles.Any(r => r.Role == Role.Admin), ct))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is the last Admin who hasn't left — grant Admin to someone else first.");
        }

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.Entry(user).CurrentValues.SetValues(new AppUser
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            HasLeft = true,
            LeftAt = now
        });

        await _db.SaveChangesAsync(ct);

        // FR-016/FR-017: leaving changes what the user may do, so their existing sign-ins are
        // re-validated (and refused outright if they hold no other role).
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
        return user;
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to mark a Student or staff member as having left.");
        }
    }
}
