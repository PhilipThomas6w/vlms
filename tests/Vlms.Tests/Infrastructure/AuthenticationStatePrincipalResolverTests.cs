using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Provisioning;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the fix for the interactive-render-mode gap (openwiki/authentication-authorization.md
/// "Known gap", STATE.md's note on commit c318bc5's checker review): resolving the caller's
/// <see cref="ClaimsPrincipal"/> via <see cref="AuthenticationStateProvider"/> rather than
/// <c>IHttpContextAccessor</c>.
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
/// <see cref="EntraCurrentUserContext"/>. That factory also runs during the OIDC
/// <c>OnTokenValidated</c> callback (<see cref="UserProvisioningService"/> ->
/// <see cref="VlmsDbContext"/> -> <see cref="ICurrentUserContext"/>), which is not a rendered
/// Razor component's DI scope — and ASP.NET Core's real <c>ServerAuthenticationStateProvider</c>
/// throws <see cref="InvalidOperationException"/> from <c>GetAuthenticationStateAsync()</c> when
/// called there, because no component has called <c>SetAuthenticationState</c> yet. Every sign-in
/// threw, so <see cref="UserProvisioningService.FindOrCreateAsync"/> never ran and no
/// <c>AppUser</c>/<c>UserRole</c> rows were ever created.
///
/// <see cref="ThrowingUntilPrimedAuthenticationStateProvider"/> reproduces that exact failure
/// mode — <c>GetAuthenticationStateAsync()</c> throws until something explicitly primes it, the
/// same shape as the real <c>ServerAuthenticationStateProvider</c> before
/// <c>SetAuthenticationState</c> is called. The tests below prove: (1) the fake genuinely
/// reproduces the throw (so it's not a no-op stand-in), (2) constructing
/// <see cref="EntraCurrentUserContext"/> against it never throws (resolution is deferred to first
/// read, not forced at construction), and (3) the real
/// <see cref="UserProvisioningService"/> -> <see cref="VlmsDbContext"/> ->
/// <see cref="ICurrentUserContext"/> chain — which never reads <c>UserId</c>/<c>HasRole</c>' —
/// completes without ever forcing that throw.
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
    /// the framework's internal <c>SetAuthenticationState</c>) has been called at least once. The
    /// OIDC <c>OnTokenValidated</c> callback runs before any component has rendered, so from this
    /// provider's perspective it is permanently "unprimed".
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
    private readonly DbContextOptions<VlmsDbContext> _options;

    public AuthenticationStatePrincipalResolverTests()
    {
        var connectionString = $"Data Source=file:vlms-authstate-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var schemaContext = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        schemaContext.Database.EnsureCreated();

        using var seed = new VlmsDbContext(_options, NullCurrentUserContext.Instance);
        seed.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "teacher-obj-id", DisplayName = "Teacher One", Email = "teacher@example.com" });
        seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Teacher });
        seed.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    [Fact]
    public void Resolve_ReturnsTheProvidersPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(EntraClaimTypes.ObjectId, "teacher-obj-id")], "TestAuth"));
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
    public void EntraCurrentUserContext_ResolvesRoleCorrectly_WhenPrincipalCameFromAuthenticationStateProvider_NotHttpContext()
    {
        // Simulates the interactive-render condition end to end: the principal reaches
        // EntraCurrentUserContext purely via AuthenticationStateProvider — no
        // IHttpContextAccessor/HttpContext exists anywhere in this test's object graph, exactly
        // the situation that failed closed (denied every role) before this fix.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(EntraClaimTypes.ObjectId, "teacher-obj-id")], "TestAuth"));
        var provider = new FakeAuthenticationStateProvider(principal);

        var resolvedPrincipal = AuthenticationStatePrincipalResolver.Resolve(provider);
        var sut = new EntraCurrentUserContext(resolvedPrincipal, _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public void ThrowingUntilPrimedProvider_GenuinelyReproducesTheRealFailureMode()
    {
        // Guards the guard: if this fake didn't actually throw when unprimed, the tests below
        // proving "construction/provisioning survives it" would be vacuous.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        Assert.Throws<InvalidOperationException>(
            () => AuthenticationStatePrincipalResolver.Resolve(provider));
    }

    [Fact]
    public void EagerlyResolvingInTheDiFactory_IsTheRegressionThisTestFileNowGuardsAgainst()
    {
        // This is the exact shape Program.cs's ICurrentUserContext factory had at commit d2adf82:
        // resolve the principal eagerly, then construct EntraCurrentUserContext from it. Proves
        // *why* that shape is unsafe — it throws the moment it runs outside a rendered
        // component's DI scope (e.g. during the OIDC OnTokenValidated callback), before
        // EntraCurrentUserContext is even reached.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        Assert.Throws<InvalidOperationException>(() =>
        {
            var principal = AuthenticationStatePrincipalResolver.Resolve(provider);
            return new EntraCurrentUserContext(principal, _options);
        });
    }

    [Fact]
    public void EntraCurrentUserContext_ConstructionFromAuthenticationStateProvider_NeverThrows_WhenUnprimed()
    {
        // The actual fix: EntraCurrentUserContext's AuthenticationStateProvider constructor must
        // defer resolution to first read of UserId/HasRole, not force it at construction. This is
        // the property the OIDC provisioning path depends on.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();

        var sut = new EntraCurrentUserContext(provider, _options);

        Assert.NotNull(sut);
    }

    [Fact]
    public void EntraCurrentUserContext_ReadingUserIdOrHasRole_StillThrows_WhenUnprimed()
    {
        // Laziness must mean "deferred to first read", not "silently swallowed". Once something
        // does read UserId/HasRole against a genuinely unprimed provider, it should still surface
        // the failure rather than hide it.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();
        var sut = new EntraCurrentUserContext(provider, _options);

        Assert.Throws<InvalidOperationException>(() => sut.UserId);
        Assert.Throws<InvalidOperationException>(() => sut.HasRole(Role.Teacher));
    }

    [Fact]
    public void EntraCurrentUserContext_ResolvesCorrectly_OnceThePrimedProviderIsRead()
    {
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();
        provider.Prime(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(EntraClaimTypes.ObjectId, "teacher-obj-id")], "TestAuth")));

        var sut = new EntraCurrentUserContext(provider, _options);

        Assert.True(sut.HasRole(Role.Teacher));
        Assert.Equal(1, sut.UserId);
    }

    [Fact]
    public async Task UserProvisioningService_FindOrCreateAsync_NeverThrows_EvenThoughItsCurrentUserContext_WouldThrowIfRead()
    {
        // The property that actually matters: reproduces the real OIDC OnTokenValidated ->
        // UserProvisioningService -> VlmsDbContext -> ICurrentUserContext chain, with the
        // ICurrentUserContext backed by a provider that throws the moment anything forces
        // resolution. UserProvisioningService only touches AppUser/UserRole directly and never
        // reads ICurrentUserContext.UserId/HasRole, so this must complete cleanly.
        var provider = new ThrowingUntilPrimedAuthenticationStateProvider();
        var currentUserContext = new EntraCurrentUserContext(provider, _options);

        using var db = new VlmsDbContext(_options, currentUserContext);
        var provisioning = new UserProvisioningService(db);

        var created = await provisioning.FindOrCreateAsync(
            "new-signin-obj-id", "New Sign-In", "new-signin@example.com");

        Assert.NotEqual(default, created.Id);
        Assert.Equal("new-signin-obj-id", created.EntraObjectId);

        var persisted = await db.AppUsers.SingleAsync(u => u.EntraObjectId == "new-signin-obj-id");
        Assert.Equal(created.Id, persisted.Id);
    }
}
