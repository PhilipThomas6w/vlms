using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards <see cref="AuthenticationStatePriming"/>: outside a rendered Razor component the server
/// <see cref="AuthenticationStateProvider"/> throws until something sets its state, which made every
/// protected page answer 500 (the authorization handlers resolve the caller through it).
/// </summary>
public sealed class AuthenticationStatePrimingTests
{
    [Fact]
    public async Task UnprimedProvider_Throws_OutsideAComponent()
    {
        var context = NewContext(new ClaimsPrincipal());

        var provider = context.RequestServices.GetRequiredService<AuthenticationStateProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAuthenticationStateAsync());
    }

    [Fact]
    public async Task Prime_HandsTheRequestUser_ToTheProvider()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42")], "test"));
        var context = NewContext(user);

        AuthenticationStatePriming.Prime(context);

        var state = await context.RequestServices.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        Assert.Same(user, state.User);
    }

    [Fact]
    public async Task Prime_HandsAnAnonymousUser_ToTheProvider()
    {
        var context = NewContext(new ClaimsPrincipal(new ClaimsIdentity()));

        AuthenticationStatePriming.Prime(context);

        var state = await context.RequestServices.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public void Program_PrimesBetweenAuthenticationAndAuthorization()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Web", "Program.cs"));

        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var priming = program.IndexOf("app.UseAuthenticationStatePriming();", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);

        Assert.True(authentication >= 0 && priming > authentication && authorization > priming);
    }

    private static DefaultHttpContext NewContext(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
        return new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build", "verify.ps1")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
