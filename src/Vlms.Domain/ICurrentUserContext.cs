namespace Vlms.Domain;

/// <summary>
/// Resolves the current caller's identity, role(s), and MFA status for use by EF Core global query
/// filters and the sensitive-data read-audit interceptor (adr/0004-sensitive-data-access-control.md).
/// Vlms.Infrastructure provides three implementations: <c>IdentityCurrentUserContext</c> (real,
/// ASP.NET Core Identity-backed — STATE.md "Identity" item), <c>SystemCurrentUserContext</c> (for
/// <c>Vlms.Jobs</c>, no signed-in human caller), and <c>NullCurrentUserContext</c> (deny-by-default,
/// for design-time/migration tooling — never wired as the runtime context).
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>The current caller's <see cref="AppUser"/> id, or null if unresolved (e.g. a background job).</summary>
    int? UserId { get; }

    /// <summary>Whether the current caller holds the given role. A caller may hold more than one role.</summary>
    bool HasRole(Role role);

    /// <summary>
    /// Whether the current caller has TOTP multi-factor authentication enabled (ASP.NET Core
    /// Identity's <c>TwoFactorEnabled</c> on its own credential row — STATE.md "Identity" item,
    /// item 4). Consulted by <see cref="Vlms.Infrastructure.Authorization.MfaRequirementHandler"/>
    /// and by <c>VlmsDbContext</c>'s <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c> query filters
    /// (adr/0004-sensitive-data-access-control.md §"MFA-gated roles", pass 7): Admin/
    /// SafeguardingOfficer must have MFA enabled before either role "counts" for any authorization
    /// decision, page or query-filter — see <c>openwiki/authentication-authorization.md</c>'s MFA
    /// section for the full, documented scope decision (enforced for those two roles specifically,
    /// offered to everyone).
    /// </summary>
    bool HasMfaEnabled { get; }
}
