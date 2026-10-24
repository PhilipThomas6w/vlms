using Vlms.Domain;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Tests.Infrastructure;

/// <summary>Configurable <see cref="ICurrentUserContext"/> test double, used throughout this test
/// project instead of the real Identity-backed <c>IdentityCurrentUserContext</c> — simpler to
/// construct directly with an arbitrary user id/role set.
///
/// <see cref="HasMfaEnabled"/> defaults to <see langword="true"/> — deliberately, so every existing
/// test written before STATE.md's "Identity" item (MFA enforcement for Admin/SafeguardingOfficer,
/// <see cref="MfaRequirementHandler"/>, VlmsDbContext's DbsCheck/ConsentSensitiveDetails query
/// filters) keeps testing exactly the property it was written to test, not incidentally start
/// failing on an orthogonal concern it was never about. Tests that specifically exercise MFA-gating
/// pass <c>hasMfaEnabled: false</c> explicitly — see <c>MfaRequirementHandlerTests</c> and the new
/// MFA-gating cases added to <c>SensitiveDataAccessControlTests</c>.</summary>
public sealed class FakeCurrentUserContext : ICurrentUserContext
{
    private readonly HashSet<Role> _roles;

    public FakeCurrentUserContext(int? userId, params Role[] roles) : this(userId, hasMfaEnabled: true, roles)
    {
    }

    public FakeCurrentUserContext(int? userId, bool hasMfaEnabled, params Role[] roles)
    {
        UserId = userId;
        HasMfaEnabled = hasMfaEnabled;
        _roles = new HashSet<Role>(roles);
    }

    public int? UserId { get; }

    public bool HasRole(Role role) => _roles.Contains(role);

    public bool HasMfaEnabled { get; }

    /// <summary>
    /// Defaults to Shrewsbury (<see cref="Chapter.ShrewsburyId"/>, the chapter every test database
    /// already has from the model's seed row), so tests written before chapters keep testing what
    /// they were written for. Chapter-separation tests set it explicitly (docs/adr/0010-chapters.md).
    /// </summary>
    public int? ActiveChapterId { get; init; } = Chapter.ShrewsburyId;

    public bool SeesAllChapters { get; init; }
}
