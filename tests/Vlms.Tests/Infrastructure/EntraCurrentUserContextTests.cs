using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="EntraCurrentUserContext.HasRole"/> against real <see cref="UserRole"/>
/// rows, using a real SQLite-in-memory <see cref="VlmsDbContext"/> — same connection-management
/// pattern as <see cref="SensitiveDataAccessControlTests"/>. A <see cref="ClaimsPrincipal"/>
/// carrying a test object-id claim stands in for the OIDC-authenticated principal Vlms.Web's
/// Program.cs would otherwise supply.
/// </summary>
public sealed class EntraCurrentUserContextTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public EntraCurrentUserContextTests()
    {
        var connectionString = $"Data Source=file:vlms-entra-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var schemaContext = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        schemaContext.Database.EnsureCreated();

        using var seed = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        seed.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "teacher-obj-id", DisplayName = "Teacher One", Email = "teacher@example.com" });
        seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Teacher });
        seed.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "no-roles-obj-id", DisplayName = "No Roles", Email = "noroles@example.com" });
        seed.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private static ClaimsPrincipal PrincipalWithObjectId(string objectId) =>
        new(new ClaimsIdentity([new Claim(EntraClaimTypes.ObjectId, objectId)], "TestAuth"));

    [Fact]
    public void HasRole_ReturnsTrue_ForARoleTheUserActuallyHolds()
    {
        var sut = new EntraCurrentUserContext(PrincipalWithObjectId("teacher-obj-id"), _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public void HasRole_ReturnsFalse_ForARoleTheUserDoesNotHold()
    {
        var sut = new EntraCurrentUserContext(PrincipalWithObjectId("no-roles-obj-id"), _options);

        Assert.False(sut.HasRole(Role.Admin));
        Assert.False(sut.HasRole(Role.Teacher));
        Assert.Equal(2, sut.UserId);
    }

    [Fact]
    public void UserId_IsNull_WhenObjectIdClaimDoesNotMatchAnyAppUser()
    {
        var sut = new EntraCurrentUserContext(PrincipalWithObjectId("unknown-obj-id"), _options);

        Assert.Null(sut.UserId);
        Assert.False(sut.HasRole(Role.Admin));
    }
}
