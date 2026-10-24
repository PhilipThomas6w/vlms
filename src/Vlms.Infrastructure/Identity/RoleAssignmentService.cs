using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// STATE.md "Admin role assignment + first-Admin bootstrap" — the Admin-only grant/revoke action
/// <see cref="AppUserProvisioningService"/>'s doc comment named as "separate, not-yet-built". Until
/// this existed, nothing in the codebase ever wrote a <see cref="UserRole"/> row, so every account
/// stayed deny-by-default forever and a fresh deployment had no usable user at all.
///
/// <b>Per chapter (FR-021, docs/adr/0010-chapters.md).</b> An Admin manages the roles of their
/// active chapter only: they see the people holding a role there (and only those roles), grant
/// Admin, Teacher, Safeguarding Officer or Parent in that chapter, and revoke them there. Roles a
/// person holds in another chapter are neither shown nor touched. The national roles (Approver,
/// National Administrator) are managed by a National Administrator on the chapters page
/// (<c>Chapters.ChapterService</c>), never here. People without a role in the chapter are not
/// listed: an Admin finds one by their exact email address (<see cref="FindByEmailAsync"/>), so no
/// chapter can browse every account on the platform.
///
/// <b>Authorization: role-checked inside the service itself</b> (defense in depth, same pattern as
/// <c>Retention.LeavingService</c>) — Admin-only for every method, including the read. Roles stay
/// the app-owned <see cref="UserRole"/> table (adr/0002-roles-as-application-claims.md), never
/// ASP.NET Core Identity's own role store.
///
/// <b>Guards, each a deliberate lockout/abuse check rather than a UI nicety:</b>
/// <list type="bullet">
/// <item>Revoking <see cref="Role.Admin"/> from the chapter's last remaining Admin who hasn't left
/// is refused — otherwise the chapter can be left with no Admin, recoverable only by a National
/// Administrator. A staff leaver keeps their role rows, which no longer count (FR-017,
/// <see cref="LeftStaffPolicy"/>), so they don't count here either.</item>
/// <item>Granting any role to an <see cref="AppUser"/> marked as having left
/// (<see cref="AppUser.HasLeft"/>) is refused — a leaver should not regain access through this page.
/// Revoking a non-staff role (Parent) from a leaver stays allowed (removing access is always safe).
/// Revoking a Teacher, Admin or Safeguarding Officer role from a leaver is refused: the row is the
/// record of their staff history, already gives them nothing, and is what keeps a staff leaver who
/// is also a parent treated as a parent who hasn't left.</item>
/// <item>Granting <see cref="Role.Student"/>, or any role to a student sign-in
/// (<see cref="Student.AppUserId"/>, or an account with no email), is refused (FR-011, adr/0007):
/// only <see cref="StudentAccountService"/> grants Student, when a linked parent creates the sign-in.</item>
/// <item>Granting a role already held in the chapter, or revoking one not held there, throws rather
/// than silently succeeding, so the page reports exactly what happened.</item>
/// </list>
///
/// <b>Role changes take effect immediately (FR-016):</b> every grant and revoke also rotates the
/// user's Identity security stamp (<see cref="SecurityStamps"/>), in the same transaction, so their
/// existing sign-ins are re-validated; <c>Security.IdentityCurrentUserContext</c> re-reads roles on
/// every check, so the change applies on the user's next action even in pages already open. An Admin
/// who changes their own roles is signed out at the next validation and signs in again.
///
/// Holding several roles at once is allowed (data-design.md: "A single user may hold more than one
/// role (e.g. a Teacher who is also the Approver)"). The Approver role still carries no safeguarding
/// privilege of its own — that separation lives in the policies and query filters, not here.
/// </summary>
public sealed class RoleAssignmentService
{
    /// <summary>The roles a chapter Admin grants and revokes. Student is granted only with a sign-in.</summary>
    public static readonly IReadOnlyList<Role> AssignableRoles = [Role.Admin, Role.Teacher, Role.SafeguardingOfficer, Role.Parent];

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;

    public RoleAssignmentService(VlmsDbContext db, ICurrentUserContext currentUser, UserManager<VlmsIdentityUser> userManager)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
    }

    /// <summary>One user and the roles they currently hold in the Admin's chapter, for the role-assignment page.</summary>
    public sealed record UserWithRoles(int UserId, string DisplayName, string Email, bool HasLeft, IReadOnlyList<Role> Roles);

    /// <summary>
    /// Every <see cref="AppUser"/> holding a role in the Admin's active chapter, with those roles,
    /// ordered by display name. Admin-only.
    /// </summary>
    public async Task<IReadOnlyList<UserWithRoles>> ListUsersAsync(CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        var roles = await _db.UserRoles.Where(r => r.ChapterId == chapterId).ToListAsync(ct);
        var rolesByUser = roles.ToLookup(r => r.UserId, r => r.Role);
        var userIds = rolesByUser.Select(g => g.Key).ToList();

        var users = await _db.AppUsers
            .Where(u => userIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        return users
            .Select(u => new UserWithRoles(
                u.Id, u.DisplayName, u.Email, u.HasLeft,
                rolesByUser[u.Id].OrderBy(r => r).ToList()))
            .ToList();
    }

    /// <summary>
    /// The adult account with exactly this email address (case-insensitive), with the roles it holds
    /// in the Admin's chapter (possibly none), or null. How an Admin finds someone new to give a role
    /// to, without listing every account on the platform. Admin-only.
    /// </summary>
    public async Task<UserWithRoles?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalised = email.Trim().ToLower();
        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Email != "" && u.Email.ToLower() == normalised, ct);
        if (user is null)
        {
            return null;
        }

        var roles = await _db.UserRoles
            .Where(r => r.UserId == user.Id && r.ChapterId == chapterId)
            .Select(r => r.Role)
            .ToListAsync(ct);
        return new UserWithRoles(user.Id, user.DisplayName, user.Email, user.HasLeft, roles.Order().ToList());
    }

    /// <summary>
    /// An account waiting for an Admin to approve it: no roles yet, not a leaver.
    /// <paramref name="FromGuardianInvite"/> is true when the account was created by accepting a
    /// guardian invite; <paramref name="LinkedStudentNames"/> are the students its
    /// <see cref="ParentGuardian"/> record is linked to (empty for a self-registered account).
    /// </summary>
    public sealed record AwaitingApproval(
        int UserId, string DisplayName, string Email, bool FromGuardianInvite, IReadOnlyList<string> LinkedStudentNames,
        IReadOnlyList<Role> CurrentRoles);

    /// <summary>
    /// STATE.md "Waiting-for-approval list on the role-assignment page", per chapter since FR-021:
    /// every account that hasn't been marked as having left, accepted one of this chapter's guardian
    /// invites, and does not yet hold <see cref="Role.Parent"/> in this chapter, ordered by display
    /// name. Admin-only. Per Philip's decision (functional.md FR-004, 11/10/2026), accepting a
    /// guardian invite grants no role; this is how the Admin sees who is waiting for that approval.
    /// An existing account that signed in to accept an invite, such as a Teacher who is also a
    /// parent, already holds other roles, which <see cref="AwaitingApproval.CurrentRoles"/> shows
    /// (this chapter's only), and approving it adds Parent to them. Accounts with no role anywhere
    /// that did not come from an invite are no longer listed (a chapter is not shown every
    /// registration on the platform): the Admin finds them by email.
    /// </summary>
    public async Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        // GuardianInvites is chapter-filtered: these are this chapter's invites.
        var inviteAccepters = _db.GuardianInvites
            .Where(i => i.ConsumedByAppUserId != null && i.ChapterId == chapterId)
            .Select(i => i.ConsumedByAppUserId!.Value);
        var parents = _db.UserRoles.Where(r => r.Role == Role.Parent && r.ChapterId == chapterId).Select(r => r.UserId);

        var waiting = await _db.AppUsers
            .Where(u => !u.HasLeft && inviteAccepters.Contains(u.Id) && !parents.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        var waitingIds = waiting.Select(u => u.Id).ToList();

        var rolesByUser = (await _db.UserRoles
            .Where(r => waitingIds.Contains(r.UserId) && r.ChapterId == chapterId)
            .ToListAsync(ct)).ToLookup(r => r.UserId, r => r.Role);

        var links = await _db.StudentGuardianLinks
            .Where(l => l.ChapterId == chapterId
                && l.ParentGuardian!.AppUserId != null && waitingIds.Contains(l.ParentGuardian.AppUserId.Value))
            .Select(l => new { UserId = l.ParentGuardian!.AppUserId!.Value, StudentName = l.Student!.Name })
            .ToListAsync(ct);
        var studentsByUser = links.ToLookup(l => l.UserId, l => l.StudentName);

        return waiting
            .Select(u => new AwaitingApproval(
                u.Id, u.DisplayName, u.Email, FromGuardianInvite: true,
                studentsByUser[u.Id].Distinct().OrderBy(n => n).ToList(),
                rolesByUser[u.Id].OrderBy(r => r).ToList()))
            .ToList();
    }

    /// <summary>
    /// Approves a guardian who accepted one of this chapter's invites by granting
    /// <see cref="Role.Parent"/> in this chapter. Admin-only. Throws if the account was not created
    /// from such an invite — any other account gets its role through <see cref="GrantAsync"/>, a
    /// deliberate choice rather than a one-click Parent grant for an unverified self-registration —
    /// or on any of <see cref="GrantAsync"/>'s own refusals (unknown user, leaver, role already held).
    /// </summary>
    public async Task ApproveGuardianAsParentAsync(int userId, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        if (!await _db.GuardianInvites.AnyAsync(i => i.ConsumedByAppUserId == userId && i.ChapterId == chapterId, ct))
        {
            throw new InvalidOperationException(
                $"AppUser {userId} did not come from one of this chapter's guardian invites — grant a role explicitly instead.");
        }

        await GrantAsync(userId, Role.Parent, ct);
    }

    /// <summary>
    /// Grants <paramref name="role"/> to <paramref name="userId"/> in the Admin's active chapter.
    /// Admin-only. Throws if the role is not one a chapter Admin grants, the user doesn't exist, has
    /// been marked as having left, is a student sign-in, or already holds the role in this chapter.
    /// </summary>
    public async Task GrantAsync(int userId, Role role, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"AppUser {userId} does not exist.");

        if (user.HasLeft)
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} has been marked as having left — roles cannot be granted to a leaver.");
        }

        // FR-011, adr/0007: Student is granted only when a linked parent creates a student sign-in
        // (StudentAccountService), never here; and a student sign-in gets no other role.
        if (role == Role.Student)
        {
            throw new InvalidOperationException(
                "The Student role is granted only when a parent creates the student's sign-in.");
        }

        if (role.IsNationalRole())
        {
            throw new InvalidOperationException(
                $"{role.DisplayName()} is a national role: a National Administrator grants it.");
        }

        // A student sign-in has no email (StudentSignInPolicy); the Student lookup alone would miss
        // one in another chapter, which this chapter's filters hide.
        if (string.IsNullOrEmpty(user.Email) || await _db.Students.AnyAsync(s => s.AppUserId == userId, ct))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is a student sign-in — it can't be given other roles.");
        }

        if (await _db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == role && r.ChapterId == chapterId, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} already holds the {role} role.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.UserRoles.Add(new UserRole { UserId = userId, Role = role, ChapterId = chapterId });
        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Revokes <paramref name="role"/> from <paramref name="userId"/> in the Admin's active chapter.
    /// Admin-only. Throws if the user doesn't hold the role there, if it is a staff role of a user who
    /// has left (FR-017), or if it would remove the chapter's last remaining Admin who hasn't left.
    /// </summary>
    public async Task RevokeAsync(int userId, Role role, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        var userRole = await _db.UserRoles.SingleOrDefaultAsync(r => r.UserId == userId && r.Role == role && r.ChapterId == chapterId, ct)
            ?? throw new InvalidOperationException($"AppUser {userId} does not hold the {role} role.");

        var hasLeft = await _db.AppUsers.AnyAsync(u => u.Id == userId && u.HasLeft, ct);

        // FR-017 (LeftStaffPolicy): a leaver's staff rows are kept as the record, count as not held,
        // and are what keep a staff leaver who is also a parent treated as a parent who hasn't left.
        if (hasLeft && LeftStaffPolicy.IsStaffRole(role))
        {
            throw new InvalidOperationException(
                $"AppUser {userId} has left: their {role} role is kept as a record and already gives them nothing.");
        }

        // Only this chapter's Admins who haven't left count towards the last-Admin guard.
        if (role == Role.Admin)
        {
            var otherWorkingAdmin = await _db.AppUsers.AnyAsync(
                u => u.Id != userId && !u.HasLeft && u.Roles.Any(r => r.Role == Role.Admin && r.ChapterId == chapterId), ct);
            if (!otherWorkingAdmin)
            {
                throw new InvalidOperationException(
                    "Cannot revoke the Admin role from the last remaining Admin — grant Admin to another user first.");
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.UserRoles.Remove(userRole);
        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
    }

    private int RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to view or change role assignments.");
        }

        return chapterId;
    }
}
