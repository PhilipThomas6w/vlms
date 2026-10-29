using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// STATE.md "Admin role assignment + first-Admin bootstrap" — the one-off step that gives a fresh
/// deployment its first Admin, so <see cref="RoleAssignmentService"/> (Admin-only) has someone to
/// call it. Without this, the only route to a first Admin would be hand-written SQL against the
/// production database.
///
/// <b>Why this is safe, and the property every change here must keep:</b>
/// <list type="bullet">
/// <item><b>It only ever works while no Admin exists.</b> Once any <see cref="UserRole"/> row with
/// <see cref="Role.Admin"/> exists, every call throws — after day one it is inert, and every later
/// role change goes through <see cref="RoleAssignmentService"/>.</item>
/// <item><b>It is wired only into the <c>Vlms.Jobs</c> console host</b> (<c>bootstrap-admin</c>
/// command), never into <c>Vlms.Web</c> — so invoking it needs shell access to the server, the same
/// trust boundary as the database itself. A web-reachable version would let whoever registers first
/// on a fresh deployment claim Admin. Guarded by <c>FirstAdminBootstrapWiringTests</c>.</item>
/// <item><b>It promotes an existing account; it never creates one.</b> The operator registers
/// through the normal <c>/Account/Register</c> page first (so the password never passes through a
/// command line or shell history), then runs the bootstrap against that email address.</item>
/// <item><b>The account's email must be confirmed.</b> Otherwise someone who registered first under
/// the operator's address (without access to that inbox) would be the one promoted.</item>
/// </list>
///
/// The new Admin still has to enrol TOTP MFA before any Admin page or sensitive data opens up
/// (<c>MfaRequirementHandler</c> and the <c>VlmsDbContext</c> query filters) — this grants the role
/// only, it bypasses nothing.
///
/// No <see cref="ICurrentUserContext"/> role check, unlike every other service: there is
/// deliberately no signed-in caller, and the "no Admin exists yet" guard is the authorization.
/// </summary>
public sealed class FirstAdminBootstrapService
{
    private readonly VlmsDbContext _db;

    public FirstAdminBootstrapService(VlmsDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Grants <see cref="Role.Admin"/> to the existing <see cref="AppUser"/> whose email matches
    /// <paramref name="email"/> (case-insensitive). Throws if an Admin already exists, if no account
    /// has that email, if its email isn't confirmed, or if the account has been marked as having left.
    /// </summary>
    public async Task<AppUser> BootstrapAsync(string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (await _db.UserRoles.AnyAsync(r => r.Role == Role.Admin, ct))
        {
            throw new InvalidOperationException(
                "An Admin already exists — bootstrap is only for a deployment with no Admin. Use the role-assignment page instead.");
        }

        var normalised = email.Trim().ToLower();
        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Email.ToLower() == normalised, ct)
            ?? throw new InvalidOperationException(
                $"No account with email '{email}' exists — register it through /Account/Register first.");

        if (!await _db.Users.AnyAsync(u => u.Id == user.Id && u.EmailConfirmed, ct))
        {
            throw new InvalidOperationException(
                $"The account for '{email}' hasn't confirmed its email address yet — use the link in the confirmation email first.");
        }

        // The first chapter (FR-021, docs/adr/0010-chapters.md): before chapters there was only one.
        _db.UserRoles.Add(new UserRole { UserId = user.Id, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// Grants <see cref="Role.NationalAdministrator"/> (FR-021, docs/adr/0010-chapters.md) to the
    /// existing account with this email, on the same terms as <see cref="BootstrapAsync"/>: only
    /// while no National Administrator exists (inert afterwards; later ones are granted on the
    /// chapters page), only to a confirmed account that has not left, and only from the
    /// <c>Vlms.Jobs</c> console (<c>bootstrap-national-admin &lt;email&gt;</c>). The role opens no
    /// chapter's records; it needs TOTP MFA before its pages open.
    /// </summary>
    public async Task<AppUser> BootstrapNationalAdministratorAsync(string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (await _db.UserRoles.AnyAsync(r => r.Role == Role.NationalAdministrator, ct))
        {
            throw new InvalidOperationException(
                "A National Administrator already exists — bootstrap is only for a deployment with none. Use the chapters page instead.");
        }

        var normalised = email.Trim().ToLower();
        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Email.ToLower() == normalised, ct)
            ?? throw new InvalidOperationException(
                $"No account with email '{email}' exists — register it through /Account/Register first.");

        if (!await _db.Users.AnyAsync(u => u.Id == user.Id && u.EmailConfirmed, ct))
        {
            throw new InvalidOperationException(
                $"The account for '{email}' hasn't confirmed its email address yet — use the link in the confirmation email first.");
        }

        _db.UserRoles.Add(new UserRole { UserId = user.Id, Role = Role.NationalAdministrator });
        await _db.SaveChangesAsync(ct);
        return user;
    }
}
