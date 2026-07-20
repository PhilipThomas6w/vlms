using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
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
/// Verifies the fix for the interactive-render-mode gap (openwiki/authentication-authorization.md
/// "Sign-in wiring", STATE.md's note on commit c318bc5's checker review): resolving the caller's
/// <see cref="ClaimsPrincipal"/> via <see cref="AuthenticationStateProvider"/> rather than
/// <c>IHttpContextAccessor</c>. Retargeted from <c>EntraCurrentUserContext</c>/
/// <c>UserProvisioningService</c> to <see cref="IdentityCurrentUserContext"/>/
/// <see cref="AppUserProvisioningService"/> (STATE.md "Identity" item) — the underlying property
/// under test (<see cref="AuthenticationStatePrincipalResolver"/> is generic, not Entra-specific,
/// and its regression-fix history fully applies to the new implementation, per that class's own
/// updated doc comment) is unchanged.
///
/// <see cref="FakeAuthenticationStateProvider"/> below never touches <c>IHttpContextAccessor</c>
/// or <see cref="Microsoft.AspNetCore.Http.HttpContext"/> at all — deliberately, to simulate
/// Blazor Server's interactive (SignalR circuit) render mode, where
/// <c>IHttpContextAccessor.HttpContext</c> is null. If role resolution only worked because some
/// HttpContext happened to be reachable, this test wouldn't prove the gap is closed; the whole
/// point is that this path has no HttpContext dependency to fail in the first place — not just
/// "it compiles".
///
/// ALSO verifies the fix for the regression that first fix introduced (checker round-trip on
/// commit d2adf82 — see STATE.md's log): Program.cs originally resolved the principal *eagerly*,
/// inside the <c>ICurrentUserContext</c> DI factory, via
/// <c>AuthenticationStatePrincipalResolver.Resolve(...)</c> before constructing
/// <see cref="IdentityCurrentUserContext"/>. Under Entra, that factory also ran during the OIDC
/// <c>OnTokenValidated</c> callback — not a rendered Razor component's DI scope. **Honest note on
/// what changed, not silently glossed over:** under Identity there is no direct equivalent of
/// <c>OnTokenValidated</c> — every account-creation code path (Register.razor,
/// Guardianship/AcceptInvite.razor) is itself a Razor component (static SSR, but still a genuinely
/// rendered component with a properly primed <see cref="AuthenticationStateProvider"/>, per
/// Microsoft's own Blazor Web App static-SSR cascading-authentication-state wiring), so the
/// *specific historical trigger* for the regression may no longer exist anywhere in this codebase's
/// Identity-era code paths. The deferred-<see cref="Lazy{T}"/> resolution shape is retained anyway,
/// deliberately, as cheap defense-in-depth against any future code path that constructs
/// <see cref="VlmsDbContext"/> (and therefore <see cref="IdentityCurrentUserContext"/>) outside a
/// properly primed scope — not because the exact same trigger is known to still exist. The tests
/// below prove the general property still holds, not that today's specific trigger is reproduced.
/// </summary>
public sealed class AuthenticationStatePrincipalResolverTests : IDisposable
{
    private sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal _principal;

        public FakeAuthenticationStateProvider(ClaimsPrincipal principal)
        {
            _principal = principal;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(_principal));
    }

    /// <summary>
    /// Reproduces the real ASP.NET Core <c>ServerAuthenticationStateProvider</c>'s behaviour
    /// before any Razor component has rendered in the circuit: <c>GetAuthenticationStateAsync()</c>
    /// throws <see cref="InvalidOperationException"/> until <see cref="Prime"/> (standing in for
    /// the framework's internal <c>SetAuthenticationState</c>) has been called at least once.
    /// </summary>
    private sealed class ThrowingUntilPrimedAuthenticationStateProvider : AuthenticationStateProvider
    {
        private Task<AuthenticationState>? _authenticationStateTask;

        public void Prime(ClaimsPrincipal principal) =>
            _authenticationStateTask = Task.FromResult(new AuthenticationState(principal));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            _authenticationStateTask ?? throw new InvalidOperationException(
                "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor " +
                "component, or after the CircuitHost has been disposed.");
    }

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public AuthenticationStatePrincipalResolverTests()
    {
        _connectionString = $"Data Source=file:vlms-authstate-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options;

        using var schemaContext = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        schemaContext.Database.EnsureCreated();

        using var seed = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        seed.Users.Add(new VlmsIdentityUser { Id = 1, UserName = "teacher@example.com", Email = "teacher@example.com" });
        seed.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Teacher One", Email = "teacher@example.com" });
        seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Teacher });
        seed.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private static ClaimsPrincipal PrincipalWithUserId(int userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"));

    [Fact]
    public void Resolve_ReturnsTheProvidersPrincipal()
    {
        var principal = PrincipalWithUserId(1);
        var provider = new FakeAuthenticationStateProvider(principal);

        var resolved = AuthenticationStatePrincipalResolver.Resolve(provider);

        Assert.Same(principal, resolved);
    }

    [Fact]
    public void Resolve_ThrowsArgumentNullException_WhenProviderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AuthenticationStatePrincipalResolver.Resolve(null!));
    }

    [Fact]
    public void IdentityCurrentUserContext_ResolvesRoleCorrectly_WhenPrincipalCameFromAuthenticationStateProvider_NotHttpContext()
    {
        // Simulates the interactive-render condition end to end: the principal reaches
        // IdentityCurrentUserContext purely via AuthenticationStateProvider — no
        // IHttpContextAccessor/HttpContext exists anywhere in this test's object graph, exactly
        // the situation that failed closed (denied every role) before this fix.
        var provider = new FakeAuthenticationStateProvider(PrincipalWithUserId(1));

        var resolvedPrincipal = AuthenticationStatePrincipalResolver.Resolve(provider);
        var sut = new IdentityCurrentUserContext(resolvedPrincipal, _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public void ThrowingUntilPrimedProvider_GenuinelyReproducesTheRealFailureMode()
    {
        // Guards the guard: if this fake didn't actually throw when unprimed, the tests below
        // proving "construction/account creation survives it" would be vacuous.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        Assert.Throws<InvalidOperationException>(
            () => AuthenticationStatePrincipalResolver.Resolve(provider));
    }

    [Fact]
    public void EagerlyResolvingInTheDiFactory_IsTheRegressionThisTestFileNowGuardsAgainst()
    {
        // This is the exact shape Program.cs's ICurrentUserContext factory had at commit d2adf82:
        // resolve the principal eagerly, then construct the current-user context from it. Proves
        // *why* that shape is unsafe — it throws the moment it runs against an unprimed provider,
        // before IdentityCurrentUserContext is even reached.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        Assert.Throws<InvalidOperationException>(() =>
        {
            var principal = AuthenticationStatePrincipalResolver.Resolve(provider);
            return new IdentityCurrentUserContext(principal, _options);
        });
    }

    [Fact]
    public void IdentityCurrentUserContext_ConstructionFromAuthenticationStateProvider_NeverThrows_WhenUnprimed()
    {
        // The actual fix: IdentityCurrentUserContext's AuthenticationStateProvider constructor must
        // defer resolution to first read of UserId/HasRole/HasMfaEnabled, not force it at
        // construction.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        var sut = new IdentityCurrentUserContext(provider, _options);

        Assert.NotNull(sut);
    }

    [Fact]
    public void IdentityCurrentUserContext_ReadingUserIdOrHasRoleOrHasMfaEnabled_StillThrows_WhenUnprimed()
    {
        // Laziness must mean "deferred to first read", not "silently swallowed".
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();
        var sut = new IdentityCurrentUserContext(provider, _options);

        Assert.Throws<InvalidOperationException>(() => sut.UserId);
        Assert.Throws<InvalidOperationException>(() => sut.HasRole(Role.Teacher));
        Assert.Throws<InvalidOperationException>(() => sut.HasMfaEnabled);
    }

    [Fact]
    public void IdentityCurrentUserContext_ResolvesCorrectly_OnceThePrimedProviderIsRead()
    {
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();
        provider.Prime(PrincipalWithUserId(1));

        var sut = new IdentityCurrentUserContext(provider, _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public async Task ConstructingVlmsDbContextAndCreatingAnAccount_NeverThrows_EvenThoughItsCurrentUserContext_WouldThrowIfRead()
    {
        // The property that actually matters for the Identity era's own account-creation code
        // paths (Register.razor, Guardianship/AcceptInvite.razor): AppUserProvisioningService
        // never reads ICurrentUserContext.UserId/HasRole/HasMfaEnabled at all (it doesn't even take
        // one as a dependency — see its own doc comment) — but it does depend on VlmsDbContext,
        // which itself always takes an ICurrentUserContext constructor argument. Wired through real
        // DI (matching how Vlms.Web's Program.cs wires it, not a hand-rolled shortcut), this proves
        // that resolving VlmsDbContext from a genuinely unprimed AuthenticationStateProvider-backed
        // ICurrentUserContext, and then creating an account through it, never forces resolution.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        services.AddScoped<ICurrentUserContext>(sp => new IdentityCurrentUserContext(provider, sp.GetRequiredService<DbContextOptions<VlmsDbContext>>()));
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        services.AddScoped<AppUserProvisioningService>();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();

        var (result, _, appUser) = await provisioning.CreateAsync("new-signin@example.com", "P@ssw0rd1!", "New Sign-In");

        Assert.True(result.Succeeded);
        Assert.NotNull(appUser);
        Assert.Equal("new-signin@example.com", appUser!.Email);

        using var verify = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        var persisted = await verify.AppUsers.SingleAsync(u => u.Id == appUser.Id);
        Assert.Equal(appUser.Id, persisted.Id);
    }
}
