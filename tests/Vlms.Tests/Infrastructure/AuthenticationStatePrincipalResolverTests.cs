using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
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
}
