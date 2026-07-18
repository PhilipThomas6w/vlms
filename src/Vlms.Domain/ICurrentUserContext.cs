namespace Vlms.Domain;

/// <summary>
/// Resolves the current caller's identity and role(s) for use by EF Core global query filters
/// and the sensitive-data read-audit interceptor (adr/0004-sensitive-data-access-control.md).
/// Vlms.Infrastructure provides two implementations: <c>EntraCurrentUserContext</c> (real,
/// Entra External ID-backed) and <c>NullCurrentUserContext</c> (deny-by-default, for design-time/
/// migration tooling — never wired as the runtime context).
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>The current caller's <see cref="AppUser"/> id, or null if unresolved (e.g. a background job).</summary>
    int? UserId { get; }

    /// <summary>Whether the current caller holds the given role. A caller may hold more than one role.</summary>
    bool HasRole(Role role);
}
