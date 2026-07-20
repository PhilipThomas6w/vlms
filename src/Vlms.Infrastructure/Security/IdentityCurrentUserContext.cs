using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Real ASP.NET Core Identity-backed <see cref="ICurrentUserContext"/> (STATE.md "Identity" item,
/// replacing the former Entra External ID-backed <c>EntraCurrentUserContext</c>): resolves the
/// signed-in <see cref="AppUser"/> (by the caller's <c>ClaimTypes.NameIdentifier</c> claim — Identity's
/// own standard user-id claim, which *is* the shared <see cref="AppUser"/>/<see cref="VlmsIdentityUser"/>
/// id, no join needed to find it), its <see cref="UserRole"/> rows, and its
/// <see cref="VlmsIdentityUser.TwoFactorEnabled"/> status, for use by <see cref="VlmsDbContext"/>'s
/// sensitive-data query filters and the role/resource-based authorization handlers
/// (adr/0002-roles-as-application-claims.md, adr/0004-sensitive-data-access-control.md).
///
/// **The interactive-render-mode DI-scoping shape from the Entra era is retained unchanged, not
/// reinvented — verified, not assumed, that it still applies:** the officially documented pattern
/// for ASP.NET Core Identity in a Blazor Web App with Server interactivity
/// (learn.microsoft.com/aspnet/core/blazor/security/server) uses
/// <c>IdentityRevalidatingAuthenticationStateProvider</c> — a subclass of
/// <c>RevalidatingServerAuthenticationStateProvider</c>, itself a subclass of the same
/// <c>ServerAuthenticationStateProvider</c> whose "throws until a component has primed it" behaviour
/// motivated <c>AuthenticationStatePrincipalResolver</c>/the deferred-<see cref="Lazy{T}"/> shape in
/// the first place (see that class's doc comment and STATE.md's log for the original c318bc5/d2adf82
/// checker round-trip that found and fixed it under Entra). Nothing about that framework behaviour
/// is Entra-specific — it is Blazor Server's own circuit lifecycle — so the exact same architecture
/// this codebase already hardened is reused here: two constructors, and the
/// <see cref="AuthenticationStateProvider"/> one defers resolution to first read of
/// <see cref="UserId"/>/<see cref="HasRole"/>/<see cref="HasMfaEnabled"/>, never at construction.
///
/// Looks up AppUser/UserRole/VlmsIdentityUser via a short-lived <see cref="VlmsDbContext"/> of its
/// own, constructed from <see cref="DbContextOptions{TContext}"/> (never the DI-resolved
/// <see cref="VlmsDbContext"/> instance) — the same pattern
/// <see cref="Auditing.SensitiveDataAuditInterceptor"/> uses, and for the same reason: this class is
/// itself a constructor dependency of <see cref="VlmsDbContext"/>, so it cannot also depend on a
/// DI-resolved <see cref="VlmsDbContext"/> without a circular resolution. Handing the lookup context
/// <see cref="NullCurrentUserContext.Instance"/> is safe and inert (nothing queried here carries a
/// sensitive-data query filter itself — AppUser/UserRole/VlmsIdentityUser are all unrestricted).
///
/// Resolution happens once per instance (lazily, on first access) and is cached for the rest of the
/// request scope — <see cref="UserId"/>/<see cref="HasRole"/>/<see cref="HasMfaEnabled"/> do not
/// re-query per call.
/// </summary>
public sealed class IdentityCurrentUserContext : ICurrentUserContext
{
    private readonly Lazy<(int? UserId, IReadOnlySet<Role> Roles, bool HasMfaEnabled)> _resolved;

    public IdentityCurrentUserContext(ClaimsPrincipal principal, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _resolved = new Lazy<(int?, IReadOnlySet<Role>, bool)>(() => Resolve(principal, dbContextOptions));
    }

    /// <summary>
    /// Defers <see cref="AuthenticationStatePrincipalResolver.Resolve"/> until first read of
    /// <see cref="UserId"/>/<see cref="HasRole"/>/<see cref="HasMfaEnabled"/> — construction itself
    /// never calls <paramref name="authenticationStateProvider"/>. This is the one Vlms.Web's
    /// Program.cs uses. See the class doc comment for why this shape is unchanged from the Entra
    /// era rather than reinvented.
    /// </summary>
    public IdentityCurrentUserContext(
        AuthenticationStateProvider authenticationStateProvider, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _resolved = new Lazy<(int?, IReadOnlySet<Role>, bool)>(() =>
        {
            var principal = AuthenticationStatePrincipalResolver.Resolve(authenticationStateProvider);
            return Resolve(principal, dbContextOptions);
        });
    }

    public int? UserId => _resolved.Value.UserId;

    public bool HasRole(Role role) => _resolved.Value.Roles.Contains(role);

    public bool HasMfaEnabled => _resolved.Value.HasMfaEnabled;

    private static (int? UserId, IReadOnlySet<Role> Roles, bool HasMfaEnabled) Resolve(
        ClaimsPrincipal principal, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        // ClaimTypes.NameIdentifier is Identity's own standard user-id claim (set by the default
        // IUserClaimsPrincipalFactory<VlmsIdentityUser> on sign-in) — its value is the shared
        // AppUser/VlmsIdentityUser id directly, not an external identifier requiring a lookup join
        // (unlike the Entra object-id claim it replaces).
        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!int.TryParse(userIdClaim, out var appUserId))
        {
            return (null, new HashSet<Role>(), false);
        }

        using var db = new VlmsDbContext(dbContextOptions, NullCurrentUserContext.Instance);

        // Confirm the AppUser row actually exists (it always should if the claim is genuine — both
        // rows are created together, Identity/AppUserProvisioningService.cs — but a caller must
        // never be trusted on the claim's say-so alone).
        var appUserExists = db.AppUsers.Any(u => u.Id == appUserId);
        if (!appUserExists)
        {
            return (null, new HashSet<Role>(), false);
        }

        var roles = db.UserRoles
            .Where(r => r.UserId == appUserId)
            .Select(r => r.Role)
            .ToHashSet();

        var hasMfaEnabled = db.Users
            .Where(u => u.Id == appUserId)
            .Select(u => u.TwoFactorEnabled)
            .SingleOrDefault();

        return (appUserId, roles, hasMfaEnabled);
    }
}
