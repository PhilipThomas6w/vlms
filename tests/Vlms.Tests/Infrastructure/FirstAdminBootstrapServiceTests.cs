using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="FirstAdminBootstrapService"/> (STATE.md "Admin role assignment + first-Admin
/// bootstrap"): a fresh database can reach a working Admin without manual SQL, and the bootstrap is
/// inert once any Admin exists. Runs under <c>Vlms.Jobs</c>'s <c>SystemCurrentUserContext</c> in
/// production; the caller context here is irrelevant to the service (it takes none) but the
/// <see cref="VlmsDbContext"/> still needs one.
/// </summary>
public sealed class FirstAdminBootstrapServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public FirstAdminBootstrapServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-bootstrap-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();

        db.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Operator", Email = "Operator@Example.com" });
        db.AppUsers.Add(new AppUser { Id = 2, DisplayName = "Someone Else", Email = "someone@example.com" });
        db.AppUsers.Add(new AppUser
        {
            Id = 3, DisplayName = "Leaver", Email = "leaver@example.com", HasLeft = true, LeftAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.AppUsers.Add(new AppUser { Id = 4, DisplayName = "Unconfirmed", Email = "unconfirmed@example.com" });
        foreach (var (id, email, confirmed) in new[]
                 {
                     (1, "Operator@Example.com", true), (2, "someone@example.com", true),
                     (3, "leaver@example.com", true), (4, "unconfirmed@example.com", false)
                 })
        {
            db.Users.Add(new VlmsIdentityUser { Id = id, UserName = email, Email = email, EmailConfirmed = confirmed });
        }
        db.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private VlmsDbContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(null));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        return services.BuildServiceProvider().GetRequiredService<VlmsDbContext>();
    }

    private async Task<List<UserRole>> AdminRows()
    {
        using var db = CreateContext();
        return await db.UserRoles.Where(r => r.Role == Role.Admin).ToListAsync();
    }

    [Fact]
    public async Task BootstrapAsync_NoAdminYet_GrantsAdminToMatchingAccount_CaseInsensitively()
    {
        using var db = CreateContext();

        var admin = await new FirstAdminBootstrapService(db).BootstrapAsync("  operator@EXAMPLE.com ");

        Assert.Equal(1, admin.Id);
        var rows = await AdminRows();
        Assert.Equal(1, Assert.Single(rows).UserId);
    }

    [Fact]
    public async Task BootstrapAsync_AdminAlreadyExists_Throws_AndGrantsNothing()
    {
        using (var db = CreateContext())
        {
            await new FirstAdminBootstrapService(db).BootstrapAsync("operator@example.com");
        }

        using var second = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FirstAdminBootstrapService(second).BootstrapAsync("someone@example.com"));

        Assert.Equal(1, Assert.Single(await AdminRows()).UserId);
    }

    [Fact]
    public async Task BootstrapAsync_UnknownEmail_Throws()
    {
        using var db = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FirstAdminBootstrapService(db).BootstrapAsync("nobody@example.com"));
        Assert.Empty(await AdminRows());
    }

    [Fact]
    public async Task BootstrapAsync_UnconfirmedEmail_Throws_AndGrantsNothing()
    {
        using var db = CreateContext();
        var service = new FirstAdminBootstrapService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BootstrapAsync("unconfirmed@example.com"));

        Assert.Contains("confirmed", ex.Message);
        Assert.Empty(await AdminRows());
    }

    [Fact]
    public async Task BootstrapAsync_Leaver_Throws()
    {
        using var db = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FirstAdminBootstrapService(db).BootstrapAsync("leaver@example.com"));
        Assert.Empty(await AdminRows());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BootstrapAsync_BlankEmail_Throws(string email)
    {
        using var db = CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => new FirstAdminBootstrapService(db).BootstrapAsync(email));
    }
}
