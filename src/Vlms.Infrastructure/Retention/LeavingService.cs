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
/// - <see cref="StepDownAsync"/> sets the equivalent <see cref="AppUser.HasLeft"/>/
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
    /// Steps down a staff member (an <see cref="AppUser"/> holding <see cref="Role.Teacher"/>,
    /// <see cref="Role.Admin"/> or <see cref="Role.SafeguardingOfficer"/> in the Admin's active
    /// chapter): sets <see cref="AppUser.HasLeft"/> to true and <see cref="AppUser.LeftAt"/> to now,
    /// and opens a <see cref="StaffStepDown"/> history row. Admin-only.
    ///
    /// <b>Takes away their staff access at once</b> (functional.md FR-017, Philip 22/10/2026,
    /// clarified 23/10/2026, amended 27/10/2026; <see cref="LeftStaffPolicy"/>): from this moment
    /// their staff roles count as not held (<c>Security.IdentityCurrentUserContext</c>), and the
    /// security stamp is rotated in the same transaction so open sessions are re-validated. With no
    /// other role they can't sign in at all; a staff member who is also a Parent keeps parent access
    /// to their own children. <see cref="AppUser.LeftAt"/> starts the retention clock.
    ///
    /// <b>Role rows are kept, not revoked.</b> They are the history (the retention report, DBS checks
    /// against a teacher, who held Admin), what marks this account as stepped-down staff rather than a
    /// parent who left, and what <see cref="ReinstateAsync"/> restores;
    /// <see cref="RoleAssignmentService.RevokeAsync"/> refuses to remove them and
    /// <see cref="RoleAssignmentService.GrantAsync"/> refuses to add staff roles meanwhile.
    ///
    /// Throws if <paramref name="userId"/> doesn't exist, holds none of the three staff roles here,
    /// is also staff of another chapter, has already stepped down, is the caller (an Admin can't lock
    /// themselves out), or is the chapter's last Admin who hasn't stepped down.
    /// </summary>
    public async Task<AppUser> StepDownAsync(int userId, CancellationToken ct = default)
    {
        var (adminId, chapterId) = RequireAdminInChapter();

        if (adminId == userId)
        {
            throw new InvalidOperationException(
                "You can't step yourself down: it would lock you out. Ask another Admin to do it.");
        }

        var user = await _db.AppUsers.SingleAsync(u => u.Id == userId, ct);

        // Per chapter (FR-021, docs/adr/0010-chapters.md): staff of this Admin's chapter only.
        var staffRows = await StaffRowsAsync(userId, ct);
        var roles = staffRows.Where(r => r.ChapterId == chapterId).Select(r => r.Role).ToList();
        if (roles.Count == 0)
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is not a Teacher, Admin or Safeguarding Officer in this chapter.");
        }

        // Stepping down is recorded on the account, so it would end their staff roles in every
        // chapter. One chapter's Admin may not do that to another chapter's staff: they remove this
        // chapter's roles instead (assumption recorded in docs/adr/0010-chapters.md).
        if (staffRows.Any(r => r.ChapterId != chapterId))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is also on the staff of another chapter. Remove their roles in this chapter instead.");
        }

        if (user.HasLeft)
        {
            throw new InvalidOperationException($"{user.DisplayName} has already stepped down.");
        }

        if (roles.Contains(Role.Admin)
            && !await _db.AppUsers.AnyAsync(u => u.Id != userId && !u.HasLeft && u.Roles.Any(r => r.Role == Role.Admin && r.ChapterId == chapterId), ct))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is the last Admin who hasn't stepped down. Grant Admin to someone else first.");
        }

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        SetSteppedDown(user, steppedDown: true, now);
        _db.StaffStepDowns.Add(new StaffStepDown
        {
            UserId = userId, ChapterId = chapterId, SteppedDownAt = now, SteppedDownByUserId = adminId
        });

        await _db.SaveChangesAsync(ct);

        // FR-016/FR-017: stepping down changes what the user may do, so their existing sign-ins are
        // re-validated (and refused outright if they hold no other role).
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
        return user;
    }

    /// <summary>
    /// Reinstates a staff member who stepped down (functional.md FR-017 as amended 27/10/2026):
    /// clears <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/> (which stops the retention
    /// clock) and closes their open <see cref="StaffStepDown"/> row with the caller and the time.
    /// Admin-only, within the Admin's active chapter.
    ///
    /// <b>Roles come back exactly as they were</b>, because their rows were never removed and
    /// nothing may add a staff role while they are stepped down (<see cref="StepDownAsync"/>): the
    /// staff roles they held in this chapter count again, and no others are granted. Their sign-in,
    /// password and authenticator are untouched, so MFA works as before. An expired DBS check does
    /// not stop reinstatement; the DBS expiry flagging picks it up as for any Teacher.
    ///
    /// <b>Chapters.</b> Stepping down is account-wide but only ever done by the Admin of the one
    /// chapter holding the person's staff roles (<see cref="StepDownAsync"/> refuses staff of several
    /// chapters, and no staff role can be granted meanwhile). So a stepped-down person has staff
    /// roles in exactly one chapter, and only that chapter's Admin reinstates them; anyone with
    /// staff roles elsewhere too (not reachable through the app) is refused.
    ///
    /// The security stamp is rotated in the same transaction (FR-016). Throws if the caller is the
    /// person, the person has not stepped down, or holds no staff role in the active chapter.
    /// </summary>
    public async Task<AppUser> ReinstateAsync(int userId, CancellationToken ct = default)
    {
        var (adminId, chapterId) = RequireAdminInChapter();

        if (adminId == userId)
        {
            throw new InvalidOperationException("You can't reinstate yourself. Ask another Admin to do it.");
        }

        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"AppUser {userId} does not exist.");

        if (!user.HasLeft)
        {
            throw new InvalidOperationException($"{user.DisplayName} has not stepped down.");
        }

        var staffRows = await StaffRowsAsync(userId, ct);
        if (!staffRows.Any(r => r.ChapterId == chapterId))
        {
            throw new InvalidOperationException($"{user.DisplayName} is not on this chapter's staff.");
        }

        if (staffRows.Any(r => r.ChapterId != chapterId))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is also on the staff of another chapter, so can't be reinstated here.");
        }

        var now = DateTime.UtcNow;
        var steppedDownAt = user.LeftAt is { } leftAt ? DateTime.SpecifyKind(leftAt, DateTimeKind.Utc) : now;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        SetSteppedDown(user, steppedDown: false, now);

        var open = await _db.StaffStepDowns
            .Where(s => s.UserId == userId && s.ReinstatedAt == null)
            .OrderByDescending(s => s.SteppedDownAt)
            .FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            var values = _db.Entry(open).CurrentValues;
            values[nameof(StaffStepDown.ReinstatedAt)] = now;
            values[nameof(StaffStepDown.ReinstatedByUserId)] = adminId;
        }
        else
        {
            // Stepped down before the history was kept: record the period from the step-down date.
            _db.StaffStepDowns.Add(new StaffStepDown
            {
                UserId = userId, ChapterId = chapterId, SteppedDownAt = steppedDownAt,
                ReinstatedAt = now, ReinstatedByUserId = adminId
            });
        }

        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
        return user;
    }

    /// <summary>One period of stepping down, with the names of the Admins who acted (null when not recorded).</summary>
    public sealed record StepDownPeriod(DateTime SteppedDownAt, string? SteppedDownBy, DateTime? ReinstatedAt, string? ReinstatedBy);

    /// <summary>A Teacher, Admin or Safeguarding Officer of the active chapter: their staff roles there and their history.</summary>
    public sealed record StaffMember(
        int UserId, string DisplayName, string Email, bool SteppedDown, IReadOnlyList<Role> Roles, IReadOnlyList<StepDownPeriod> History);

    /// <summary>
    /// Everyone holding a staff role in the Admin's active chapter, stepped down or not, ordered by
    /// name, each with their stepping-down history in this chapter (oldest first). Admin-only.
    /// </summary>
    public async Task<IReadOnlyList<StaffMember>> ListStaffAsync(CancellationToken ct = default)
    {
        var (_, chapterId) = RequireAdminInChapter();

        var staffRoles = await _db.UserRoles
            .Where(r => (r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer) && r.ChapterId == chapterId)
            .Select(r => new { r.UserId, r.Role })
            .ToListAsync(ct);
        var rolesByUser = staffRoles.ToLookup(r => r.UserId, r => r.Role);
        var userIds = rolesByUser.Select(g => g.Key).ToList();

        var users = await _db.AppUsers.Where(u => userIds.Contains(u.Id)).OrderBy(u => u.DisplayName).ToListAsync(ct);
        var periods = await _db.StaffStepDowns
            .Where(s => s.ChapterId == chapterId && userIds.Contains(s.UserId))
            .OrderBy(s => s.SteppedDownAt)
            .ToListAsync(ct);

        var actorIds = periods.SelectMany(p => new[] { p.SteppedDownByUserId, p.ReinstatedByUserId })
            .OfType<int>().Distinct().ToList();
        var names = await _db.AppUsers.Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? NameOf(int? id) => id is int value ? names.GetValueOrDefault(value) : null;
        var periodsByUser = periods.ToLookup(p => p.UserId);

        return users
            .Select(u => new StaffMember(
                u.Id, u.DisplayName, u.Email, u.HasLeft,
                rolesByUser[u.Id].Order().ToList(),
                periodsByUser[u.Id]
                    // An Admin whose account was deleted under the retention rules reads as a former member of
                    // staff; a step-down copied in before this history existed names nobody (docs/adr/0014).
                    .Select(p => new StepDownPeriod(
                        p.SteppedDownAt, FormerStaff.Or(NameOf(p.SteppedDownByUserId), !p.RecordedBeforeHistory),
                        p.ReinstatedAt, FormerStaff.Or(NameOf(p.ReinstatedByUserId), p.ReinstatedAt is not null)))
                    .ToList()))
            .ToList();
    }

    private async Task<List<(Role Role, int? ChapterId)>> StaffRowsAsync(int userId, CancellationToken ct)
    {
        var rows = await _db.UserRoles
            .Where(r => r.UserId == userId && (r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer))
            .Select(r => new { r.Role, r.ChapterId })
            .ToListAsync(ct);
        return rows.Select(r => (r.Role, r.ChapterId)).ToList();
    }

    private void SetSteppedDown(AppUser user, bool steppedDown, DateTime now) =>
        _db.Entry(user).CurrentValues.SetValues(new AppUser
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            HasLeft = steppedDown,
            LeftAt = steppedDown ? now : null
        });

    private (int AdminId, int ChapterId) RequireAdminInChapter()
    {
        RequireAdmin();
        if (_currentUser.UserId is not int adminId || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must be a signed-in Admin of a chapter.");
        }

        return (adminId, chapterId);
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to mark a student as having left or to step down or reinstate staff.");
        }
    }
}