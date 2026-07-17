using Vlms.Domain;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Deny-by-default placeholder implementation of <see cref="ICurrentUserContext"/> — no user,
/// no roles. The real Entra External ID-backed implementation is a later STATE.md item
/// (Microsoft Entra External ID integration); this exists so the data model and its query
/// filters/audit interceptor can be built and tested now without depending on that work.
/// </summary>
public sealed class NullCurrentUserContext : ICurrentUserContext
{
    public static readonly NullCurrentUserContext Instance = new();

    public int? UserId => null;

    public bool HasRole(Role role) => false;
}
