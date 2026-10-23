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
/// <b>Authorization: role-checked inside the service itself</b> (defense in depth, same pattern as
/// <c>Retention.LeavingService</c>) — Admin-only for every method, including the read. Roles stay
/// the app-owned <see cref="UserRole"/> table (adr/0002-roles-as-application-claims.md), never
/// ASP.NET Core Identity's own role store.
///
/// <b>Guards, each a deliberate lockout/abuse check rather than a UI nicety:</b>
/// <list type="bullet">
/// <item>Revoking <see cref="Role.Admin"/> from the last remaining Admin who hasn't left is refused —
/// otherwise the system can be left with no Admin, recoverable only through
/// <see cref="FirstAdminBootstrapService"/> on the server. A staff leaver keeps their role rows,
/// which no longer count (FR-017, <see cref="LeftStaffPolicy"/>), so they don't count here either.</item>
/// <item>Granting any role to an <see cref="AppUser"/> marked as having left
/// (<see cref="AppUser.HasLeft"/>) is refused — a leaver should not regain access through this page.
/// Revoking a non-staff role (Parent, Approver) from a leaver stays allowed (removing access is
/// always safe). Revoking a Teacher, Admin or Safeguarding Officer role from a leaver is refused: the
/// row is the record of their staff history, already gives them nothing, and is what keeps a staff
/// leaver who is also a parent treated as a parent who hasn't left.</item>
/// <item>Granting <see cref="Role.Student"/>, or any role to a student sign-in
/// (<see cref="Student.AppUserId"/>), is refused (FR-011, adr/0007): only
/// <see cref="StudentAccountService"/> grants Student, when a linked parent creates the sign-in.</item>
/// <item>Granting a role already held, or revoking one not held, throws rather than silently
/// succeeding, so the page reports exactly what happened.</item>
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
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;

    public RoleAssignmentService(VlmsDbContext db, ICurrentUserContext currentUser, UserManager<VlmsIdentityUser> userManager)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
    }

    /// <summary>One user and the roles they currently hold, for the role-assignment page.</summary>
    public sealed record UserWithRoles(int UserId, string DisplayName, string Email, bool HasLeft, IReadOnlyList<Role> Roles);

    /// <summary>Every <see cref="AppUser"/> with their current roles, ordered by display name. Admin-only.</summary>
    public async Task<IReadOnlyList<UserWithRoles>> ListUsersAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var users = await _db.AppUsers
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        var roles = await _db.UserRoles.ToListAsync(ct);
        var rolesByUser = roles.ToLookup(r => r.UserId, r => r.Role);

        return users
            .Select(u => new UserWithRoles(
                u.Id, u.DisplayName, u.Email, u.HasLeft,
                rolesByUser[u.Id].OrderBy(r => r).ToList()))
            .ToList();
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
    /// STATE.md "Waiting-for-approval list on the role-assignment page" — every account that hasn't
    /// been marked as having left and either has no roles, or accepted a guardian invite and does not
    /// yet hold <see cref="Role.Parent"/>, ordered by display name. Admin-only. Per Philip's decision
    /// (functional.md FR-004, 11/10/2026), accepting a guardian invite grants no role; this is how the
    /// Admin sees who is waiting for that approval. The second arm (FR-004, 22/10/2026) covers an
    /// existing account that signed in to accept an invite, such as a Teacher who is also a parent:
    /// it already holds other roles, which <see cref="AwaitingApproval.CurrentRoles"/> shows, and
    /// approving it adds Parent to them.
    /// </summary>
    public async Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var usersWithRoles = _db.UserRoles.Select(r => r.UserId);
        var parents = _db.UserRoles.Where(r => r.Role == Role.Parent).Select(r => r.UserId);
        var inviteAccepters = _db.GuardianInvites
            .Where(i => i.ConsumedByAppUserId != null)
            .Select(i => i.ConsumedByAppUserId!.Value);

        var waiting = await _db.AppUsers
            .Where(u => !u.HasLeft
                && (!usersWithRoles.Contains(u.Id)
                    || (inviteAccepters.Contains(u.Id) && !parents.Contains(u.Id))))
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

        var waitingIds = waiting.Select(u => u.Id).ToList();

        var inviteUserIds = (await _db.GuardianInvites
            .Where(i => i.ConsumedByAppUserId != null && waitingIds.Contains(i.ConsumedByAppUserId.Value))
            .Select(i => i.ConsumedByAppUserId!.Value)
            .ToListAsync(ct)).ToHashSet();

        var rolesByUser = (await _db.UserRoles
            .Where(r => waitingIds.Contains(r.UserId))
            .ToListAsync(ct)).ToLookup(r => r.UserId, r => r.Role);

        var links = await _db.StudentGuardianLinks
            .Where(l => l.ParentGuardian!.AppUserId != null && waitingIds.Contains(l.ParentGuardian.AppUserId.Value))
            .Select(l => new { UserId = l.ParentGuardian!.AppUserId!.Value, StudentName = l.Student!.Name })
            .ToListAsync(ct);
        var studentsByUser = links.ToLookup(l => l.UserId, l => l.StudentName);

        return waiting
            .Select(u => new AwaitingApproval(
                u.Id, u.DisplayName, u.Email, inviteUserIds.Contains(u.Id),
                studentsByUser[u.Id].Distinct().OrderBy(n => n).ToList(),
                rolesByUser[u.Id].OrderBy(r => r).ToList()))
            .ToList();
    }

    /// <summary>
    /// Approves a guardian who accepted an invite by granting <see cref="Role.Parent"/>. Admin-only.
    /// Throws if the account was not created from a guardian invite — any other account gets its
    /// role through <see cref="GrantAsync"/>, a deliberate choice rather than a one-click Parent
    /// grant for an unverified self-registration — or on any of <see cref="GrantAsync"/>'s own
    /// refusals (unknown user, leaver, role already held).
    /// </summary>
    public async Task ApproveGuardianAsParentAsync(int userId, CancellationToken ct = default)
    {
        RequireAdmin();

        if (!await _db.GuardianInvites.AnyAsync(i => i.ConsumedByAppUserId == userId, ct))
        {
            throw new InvalidOperationException(
                $"AppUser {userId} did not come from a guardian invite — grant a role explicitly instead.");
        }

        await GrantAsync(userId, Role.Parent, ct);
    }

    /// <summary>
    /// Grants <paramref name="role"/> to <paramref name="userId"/>. Admin-only. Throws if the user
    /// doesn't exist, has been marked as having left, or already holds the role.
    /// </summary>
    public async Task GrantAsync(int userId, Role role, CancellationToken ct = default)
    {
        RequireAdmin();

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

        if (await _db.Students.AnyAsync(s => s.AppUserId == userId, ct))
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is a student sign-in — it can't be given other roles.");
        }

        if (await _db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == role, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} already holds the {role} role.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.UserRoles.Add(new UserRole { UserId = userId, Role = role });
        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Revokes <paramref name="role"/> from <paramref name="userId"/>. Admin-only. Throws if the
    /// user doesn't hold the role, if it is a staff role of a user who has left (FR-017), or if it
    /// would remove the last remaining Admin who hasn't left.
    /// </summary>
    public async Task RevokeAsync(int userId, Role role, CancellationToken ct = default)
    {
        RequireAdmin();

        var userRole = await _db.UserRoles.SingleOrDefaultAsync(r => r.UserId == userId && r.Role == role, ct)
            ?? throw new InvalidOperationException($"AppUser {userId} does not hold the {role} role.");

        var hasLeft = await _db.AppUsers.AnyAsync(u => u.Id == userId && u.HasLeft, ct);

        // FR-017 (LeftStaffPolicy): a leaver's staff rows are kept as the record, count as not held,
        // and are what keep a staff leaver who is also a parent treated as a parent who hasn't left.
        if (hasLeft && LeftStaffPolicy.IsStaffRole(role))
        {
            throw new InvalidOperationException(
                $"AppUser {userId} has left: their {role} role is kept as a record and already gives them nothing.");
        }

        // Only Admins who haven't left count towards the last-Admin guard.
        if (role == Role.Admin)
        {
            var otherWorkingAdmin = await _db.AppUsers.AnyAsync(
                u => u.Id != userId && !u.HasLeft && u.Roles.Any(r => r.Role == Role.Admin), ct);
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

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to view or change role assignments.");
        }
    }
}
