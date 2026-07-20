using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Replaces the retired <c>UserProvisioningServiceTests</c> (STATE.md "Identity" item — see
/// <see cref="AppUserProvisioningService"/>'s own doc comment for why the retired class's premise,
/// "find-or-create against a federated OIDC claim on every sign-in", has no equivalent under local
/// ASP.NET Core Identity, so this is a genuinely new test file testing a genuinely new service, not
/// a rename). Covers the property STATE.md's item explicitly asked to be re-derived, not dropped: a
/// freshly created account gets zero <see cref="UserRole"/> rows (deny-by-default) — plus the new
/// service's own atomic-pairing and duplicate/validation-failure behaviour.
///
/// Wired through real DI (<c>AddIdentityCore&lt;VlmsIdentityUser&gt;().AddEntityFrameworkStores&lt;VlmsDbContext&gt;()</c>),
/// not a hand-rolled <c>UserManager</c> construction — matching how Vlms.Web's Program.cs actually
/// wires it (see <c>AuthenticationStatePrincipalResolverTests</c>' identical approach for the same
/// reason).
/// </summary>
public sealed class AppUserProvisioningServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public AppUserProvisioningServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-appuserprovisioning-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schemaContext = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);
        schemaContext.Database.EnsureCreated();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        services.AddSingleton<ICurrentUserContext>(NullCurrentUserContext.Instance);
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        services.AddScoped<AppUserProvisioningService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CreateAsync_Succeeds_CreatesPairedIdentityUserAndAppUser_WithZeroRoles()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();

        var (result, identityUser, appUser) = await sut.CreateAsync("alex@example.com", "Str0ng!Passw0rd", "Alex Teacher");

        Assert.True(result.Succeeded);
        Assert.NotNull(identityUser);
        Assert.NotNull(appUser);

        // The shared-primary-key pairing invariant (AppUser's own doc comment) — both rows carry
        // the same id, and it's the Identity store's own generated value, not an app-computed one.
        Assert.Equal(identityUser!.Id, appUser!.Id);
        Assert.NotEqual(0, appUser.Id);
        Assert.Equal("Alex Teacher", appUser.DisplayName);
        Assert.Equal("alex@example.com", appUser.Email);

        using var verify = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);

        var persistedAppUser = await verify.AppUsers.SingleAsync(u => u.Email == "alex@example.com");
        Assert.Equal(appUser.Id, persistedAppUser.Id);

        // Deny-by-default (re-derived from the retired UserProvisioningService — see
        // AppUserProvisioningService's doc comment): zero UserRole rows on creation.
        Assert.Empty(verify.UserRoles.Where(r => r.UserId == appUser.Id));
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_Fails_CreatesNoSecondRowPair()
    {
        using var provider = BuildProvider();

        using (var firstScope = provider.CreateScope())
        {
            var first = firstScope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();
            var (firstResult, _, _) = await first.CreateAsync("dup@example.com", "Str0ng!Passw0rd", "First");
            Assert.True(firstResult.Succeeded);
        }

        using var secondScope = provider.CreateScope();
        var second = secondScope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();
        var (secondResult, secondIdentityUser, secondAppUser) = await second.CreateAsync("dup@example.com", "An0ther!Passw0rd", "Second");

        Assert.False(secondResult.Succeeded);
        Assert.Null(secondIdentityUser);
        Assert.Null(secondAppUser);

        using var verify = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);
        Assert.Single(verify.AppUsers.Where(u => u.Email == "dup@example.com"));
    }

    [Fact]
    public async Task CreateAsync_WeakPassword_Fails_CreatesNoRowsAtAll()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();

        var (result, identityUser, appUser) = await sut.CreateAsync("weak@example.com", "abc", "Weak Password");

        Assert.False(result.Succeeded);
        Assert.Null(identityUser);
        Assert.Null(appUser);

        using var verify = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);
        Assert.Empty(verify.AppUsers.Where(u => u.Email == "weak@example.com"));
        Assert.Empty(verify.Users.Where(u => u.Email == "weak@example.com"));
    }
}
