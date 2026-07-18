using Vlms.Domain;

namespace Vlms.Tests.Infrastructure;

/// <summary>Configurable <see cref="ICurrentUserContext"/> test double, used throughout this test
/// project instead of the real Entra-backed <c>EntraCurrentUserContext</c> — simpler to construct
/// directly with an arbitrary user id/role set.</summary>
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
