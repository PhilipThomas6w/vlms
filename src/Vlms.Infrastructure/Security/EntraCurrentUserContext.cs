using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Real Microsoft Entra External ID-backed <see cref="ICurrentUserContext"/>: resolves the
/// signed-in <see cref="AppUser"/> (by the caller's Entra object id claim) and its
/// <see cref="UserRole"/> rows, for use by <see cref="VlmsDbContext"/>'s sensitive-data query
/// filters and by the role/resource-based authorization handlers
/// (adr/0002-roles-as-application-claims.md, adr/0004-sensitive-data-access-control.md).
///
/// Two constructors:
/// - <see cref="EntraCurrentUserContext(ClaimsPrincipal, DbContextOptions{VlmsDbContext})"/> takes
///   an already-resolved <see cref="ClaimsPrincipal"/> directly — trivially testable, no ASP.NET
///   Core hosting needed.
/// - <see cref="EntraCurrentUserContext(AuthenticationStateProvider, DbContextOptions{VlmsDbContext})"/>
///   takes the <see cref="AuthenticationStateProvider"/> itself and defers calling
///   <see cref="AuthenticationStatePrincipalResolver.Resolve"/> until the principal is actually
///   needed (first read of <see cref="UserId"/>/<see cref="HasRole"/>). This is the one
///   Vlms.Web's Program.cs uses, and it is deliberately NOT "resolve eagerly in the DI factory,
///   then pass the ClaimsPrincipal in" — that shape was tried and regressed sign-in (see
///   openwiki/authentication-authorization.md and STATE.md's log for the checker round-trip that
///   found it): ASP.NET Core's built-in <c>ServerAuthenticationStateProvider</c> throws
///   <see cref="InvalidOperationException"/> from <c>GetAuthenticationStateAsync()</c> when called
///   outside a rendered Razor component's DI scope — which is exactly the scope the OIDC
///   <c>OnTokenValidated</c> handler runs in when it resolves
///   <see cref="Provisioning.UserProvisioningService"/> → <see cref="VlmsDbContext"/> →
///   <see cref="ICurrentUserContext"/>. <see cref="Provisioning.UserProvisioningService"/> never
///   reads <see cref="UserId"/>/<see cref="HasRole"/>, so with resolution deferred to first read,
///   that path never forces it and never throws.
///
/// Looks up AppUser/UserRole via a short-lived <see cref="VlmsDbContext"/> of its own,
/// constructed from <see cref="DbContextOptions{TContext}"/> (never the DI-resolved
/// <see cref="VlmsDbContext"/> instance) — the same pattern
/// <see cref="Auditing.SensitiveDataAuditInterceptor"/> uses, and for the same reason: this
/// class is itself a constructor dependency of <see cref="VlmsDbContext"/>, so it cannot also
/// depend on a DI-resolved <see cref="VlmsDbContext"/> without a circular resolution. Neither
/// <see cref="AppUser"/> nor <see cref="UserRole"/> carries a sensitive-data query filter, so
/// handing the lookup context <see cref="NullCurrentUserContext.Instance"/> is safe and inert.
///
/// Resolution happens once per instance (lazily, on first access) and is cached for the rest of
/// the request scope — <see cref="UserId"/>/<see cref="HasRole"/> do not re-query per call.
/// </summary>
public sealed class EntraCurrentUserContext : ICurrentUserContext
{
    private readonly Lazy<(int? UserId, IReadOnlySet<Role> Roles)> _resolved;

    public EntraCurrentUserContext(ClaimsPrincipal principal, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _resolved = new Lazy<(int?, IReadOnlySet<Role>)>(() => Resolve(principal, dbContextOptions));
    }

    /// <summary>
    /// Defers <see cref="AuthenticationStatePrincipalResolver.Resolve"/> until first read of
    /// <see cref="UserId"/>/<see cref="HasRole"/> — construction itself never calls
    /// <paramref name="authenticationStateProvider"/>. See the class doc comment for why this
    /// matters for the OIDC provisioning path.
    /// </summary>
    public EntraCurrentUserContext(
        AuthenticationStateProvider authenticationStateProvider, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _resolved = new Lazy<(int?, IReadOnlySet<Role>)>(() =>
        {
            var principal = AuthenticationStatePrincipalResolver.Resolve(authenticationStateProvider);
            return Resolve(principal, dbContextOptions);
        });
    }

    public int? UserId => _resolved.Value.UserId;

    public bool HasRole(Role role) => _resolved.Value.Roles.Contains(role);

    private static (int? UserId, IReadOnlySet<Role> Roles) Resolve(
        ClaimsPrincipal principal, DbContextOptions<VlmsDbContext> dbContextOptions)
    {
        var entraObjectId = principal.FindFirst(EntraClaimTypes.ObjectId)?.Value
            ?? principal.FindFirst(EntraClaimTypes.ObjectIdShort)?.Value;

        if (string.IsNullOrEmpty(entraObjectId))
        {
            return (null, new HashSet<Role>());
        }

        using var db = new VlmsDbContext(dbContextOptions, NullCurrentUserContext.Instance);

        var appUserId = db.AppUsers
            .Where(u => u.EntraObjectId == entraObjectId)
            .Select(u => (int?)u.Id)
            .SingleOrDefault();

        if (appUserId is null)
        {
            return (null, new HashSet<Role>());
        }

        var roles = db.UserRoles
            .Where(r => r.UserId == appUserId)
            .Select(r => r.Role)
            .ToHashSet();

        return (appUserId, roles);
    }
}
