using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Provisioning;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="UserProvisioningService"/>'s find-or-create behaviour, including the
/// deny-by-default rule that a freshly created <see cref="Vlms.Domain.AppUser"/> gets zero
/// <see cref="Vlms.Domain.UserRole"/> rows.
///
/// Uses a named, shared-cache SQLite in-memory database — the same connection-management
/// approach as <see cref="SensitiveDataAccessControlTests"/> — so each simulated call opens its
/// own <see cref="VlmsDbContext"/> against the same underlying data, matching how production
/// (separate scoped contexts per request) behaves.
/// </summary>
public sealed class UserProvisioningServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public UserProvisioningServiceTests()
    {
        var connectionString = $"Data Source=file:vlms-provisioning-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var schemaContext = CreateContext();
        schemaContext.Database.EnsureCreated();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private VlmsDbContext CreateContext() => new(_options, NullCurrentUserContext.Instance);

    [Fact]
    public async Task FindOrCreateAsync_FirstSignIn_CreatesAppUser_WithZeroRoles()
    {
        int createdId;
        using (var context = CreateContext())
        {
            var sut = new UserProvisioningService(context);

            var appUser = await sut.FindOrCreateAsync("entra-object-1", "Alex Teacher", "alex@example.com");

            Assert.Equal("entra-object-1", appUser.EntraObjectId);
            Assert.Equal("Alex Teacher", appUser.DisplayName);
            Assert.Equal("alex@example.com", appUser.Email);
            createdId = appUser.Id;
        }

        using var verify = CreateContext();
        var persisted = await verify.AppUsers.SingleAsync(u => u.EntraObjectId == "entra-object-1");
        Assert.Equal(createdId, persisted.Id);
        Assert.Empty(verify.UserRoles.Where(r => r.UserId == persisted.Id));
    }

    [Fact]
    public async Task FindOrCreateAsync_SecondCallSameObjectId_ReturnsExistingRow_DoesNotDuplicate()
    {
        int firstId;
        using (var context = CreateContext())
        {
            var sut = new UserProvisioningService(context);
            var first = await sut.FindOrCreateAsync("entra-object-2", "Sam Parent", "sam@example.com");
            firstId = first.Id;
        }

        using (var context = CreateContext())
        {
            var sut = new UserProvisioningService(context);
            var second = await sut.FindOrCreateAsync("entra-object-2", "Sam Parent", "sam@example.com");
            Assert.Equal(firstId, second.Id);
        }

        using var verify = CreateContext();
        Assert.Single(verify.AppUsers.Where(u => u.EntraObjectId == "entra-object-2"));
    }
}
