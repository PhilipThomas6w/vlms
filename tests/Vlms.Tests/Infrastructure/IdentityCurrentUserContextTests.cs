using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Replaces the retired <c>EntraCurrentUserContextTests</c> (STATE.md "Identity" item) — same
/// premise, retargeted: verifies <see cref="IdentityCurrentUserContext.HasRole"/>/
/// <see cref="IdentityCurrentUserContext.UserId"/> against real <see cref="UserRole"/> rows, using a
/// real SQLite-in-memory <see cref="VlmsDbContext"/> — same connection-management pattern as
/// <see cref="SensitiveDataAccessControlTests"/>. A <see cref="ClaimsPrincipal"/> carrying
/// <see cref="ClaimTypes.NameIdentifier"/> (Identity's own standard user-id claim) stands in for the
/// cookie-authenticated principal Vlms.Web's Program.cs would otherwise supply. New coverage beyond
/// the retired file: <see cref="IdentityCurrentUserContext.HasMfaEnabled"/>, resolved from the
/// paired <see cref="VlmsIdentityUser.TwoFactorEnabled"/> row (shared primary key — see
/// <see cref="AppUser"/>'s doc comment).
/// </summary>
public sealed class IdentityCurrentUserContextTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public IdentityCurrentUserContextTests()
    {
        var connectionString = $"Data Source=file:vlms-identity-cuc-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var schemaContext = new VlmsDbContext(_options, TestSeedUserContext.Instance);
        schemaContext.Database.EnsureCreated();

        using var seed = new VlmsDbContext(_options, TestSeedUserContext.Instance);
        seed.Users.Add(new VlmsIdentityUser { Id = 1, UserName = "teacher@example.com", Email = "teacher@example.com", TwoFactorEnabled = false });
        seed.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Teacher One", Email = "teacher@example.com" });
        seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Teacher });

        seed.Users.Add(new VlmsIdentityUser { Id = 2, UserName = "noroles@example.com", Email = "noroles@example.com", TwoFactorEnabled = false });
        seed.AppUsers.Add(new AppUser { Id = 2, DisplayName = "No Roles", Email = "noroles@example.com" });

        seed.Users.Add(new VlmsIdentityUser { Id = 3, UserName = "admin-mfa@example.com", Email = "admin-mfa@example.com", TwoFactorEnabled = true });
        seed.AppUsers.Add(new AppUser { Id = 3, DisplayName = "Admin With MFA", Email = "admin-mfa@example.com" });
        seed.UserRoles.Add(new UserRole { UserId = 3, Role = Role.Admin });

        seed.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private static ClaimsPrincipal PrincipalWithUserId(int userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"));

    [Fact]
    public void HasRole_ReturnsTrue_ForARoleTheUserActuallyHolds()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(1), _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public void HasRole_ReturnsFalse_ForARoleTheUserDoesNotHold()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(2), _options);

        Assert.False(sut.HasRole(Role.Admin));
        Assert.False(sut.HasRole(Role.Teacher));
        Assert.Equal(2, sut.UserId);
    }

    [Fact]
    public void UserId_IsNull_WhenClaimDoesNotMatchAnyAppUser()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(9999), _options);

        Assert.Null(sut.UserId);
        Assert.False(sut.HasRole(Role.Admin));
    }

    [Fact]
    public void UserId_IsNull_WhenClaimIsNotAnInteger()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "not-an-int")], "TestAuth"));
        var sut = new IdentityCurrentUserContext(principal, _options);

        Assert.Null(sut.UserId);
    }

    [Fact]
    public void HasMfaEnabled_ReturnsFalse_ForAUserWhoNeverEnrolled()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(1), _options);

        Assert.False(sut.HasMfaEnabled);
    }

    [Fact]
    public void HasMfaEnabled_ReturnsTrue_ForAUserWithTotpEnabled()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(3), _options);

        Assert.True(sut.HasMfaEnabled);
        Assert.True(sut.HasRole(Role.Admin));
    }

    [Fact]
    public void HasMfaEnabled_ReturnsFalse_WhenPrincipalDoesNotResolveToAnAppUser()
    {
        var sut = new IdentityCurrentUserContext(PrincipalWithUserId(9999), _options);

        Assert.False(sut.HasMfaEnabled);
    }
}
