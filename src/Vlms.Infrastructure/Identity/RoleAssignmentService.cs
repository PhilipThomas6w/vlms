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
/// <item>Revoking <see cref="Role.Admin"/> from the last remaining Admin is refused — otherwise the
/// system can be left with no Admin, recoverable only through <see cref="FirstAdminBootstrapService"/>
/// on the server.</item>
/// <item>Granting any role to an <see cref="AppUser"/> marked as having left
/// (<see cref="AppUser.HasLeft"/>) is refused — a leaver should not regain access through this page.
/// Revoking from a leaver stays allowed (removing access is always safe).</item>
/// <item>Granting a role already held, or revoking one not held, throws rather than silently
/// succeeding, so the page reports exactly what happened.</item>
/// </list>
///
/// Holding several roles at once is allowed (data-design.md: "A single user may hold more than one
/// role (e.g. a Teacher who is also the Approver)"). The Approver role still carries no safeguarding
/// privilege of its own — that separation lives in the policies and query filters, not here.
/// </summary>
public sealed class RoleAssignmentService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public RoleAssignmentService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
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

        if (await _db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == role, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} already holds the {role} role.");
        }

        _db.UserRoles.Add(new UserRole { UserId = userId, Role = role });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Revokes <paramref name="role"/> from <paramref name="userId"/>. Admin-only. Throws if the
    /// user doesn't hold the role, or if it would remove the last remaining Admin.
    /// </summary>
    public async Task RevokeAsync(int userId, Role role, CancellationToken ct = default)
    {
        RequireAdmin();

        var userRole = await _db.UserRoles.SingleOrDefaultAsync(r => r.UserId == userId && r.Role == role, ct)
            ?? throw new InvalidOperationException($"AppUser {userId} does not hold the {role} role.");

        if (role == Role.Admin)
        {
            var adminCount = await _db.UserRoles.CountAsync(r => r.Role == Role.Admin, ct);
            if (adminCount <= 1)
            {
                throw new InvalidOperationException(
                    "Cannot revoke the Admin role from the last remaining Admin — grant Admin to another user first.");
            }
        }

        _db.UserRoles.Remove(userRole);
        await _db.SaveChangesAsync(ct);
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to view or change role assignments.");
        }
    }
}
