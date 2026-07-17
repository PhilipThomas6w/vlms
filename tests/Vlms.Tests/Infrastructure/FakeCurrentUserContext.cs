using Vlms.Domain;

namespace Vlms.Tests.Infrastructure;

/// <summary>Configurable <see cref="ICurrentUserContext"/> test double — a real Entra-backed
/// implementation is a later STATE.md item; this stands in for it in tests.</summary>
public sealed class FakeCurrentUserContext : ICurrentUserContext
{
    private readonly HashSet<Role> _roles;

    public FakeCurrentUserContext(int? userId, params Role[] roles)
    {
        UserId = userId;
        _roles = new HashSet<Role>(roles);
    }

    public int? UserId { get; }

    public bool HasRole(Role role) => _roles.Contains(role);
}
