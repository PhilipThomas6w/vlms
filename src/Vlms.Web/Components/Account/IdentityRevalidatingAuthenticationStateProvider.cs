using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vlms.Infrastructure.Identity;

namespace Vlms.Web.Components.Account;

/// <summary>
/// Adapted from the official ASP.NET Core Identity + Blazor Web App template
/// (`dotnet new blazor -au Individual`, `Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`)
/// — verified against current Microsoft Learn guidance (learn.microsoft.com/aspnet/core/blazor/security/server)
/// rather than hand-rolled, per CLAUDE.md "verify, don't invent" and STATE.md "Identity" item 1's
/// explicit instruction to confirm the interactive-render-mode pattern rather than assume it.
///
/// A server-side <see cref="AuthenticationStateProvider"/> (registered as the DI
/// <see cref="AuthenticationStateProvider"/> in Program.cs) that re-validates the connected caller's
/// security stamp every <see cref="RevalidationInterval"/> for a live circuit — so a password
/// change/account lockout/security-stamp reset takes effect for an already-connected interactive
/// session, not just on the next full page load. This is layered *underneath*
/// <see cref="Vlms.Infrastructure.Security.IdentityCurrentUserContext"/>, which is what this
/// codebase's own authorization/query-filter code actually consumes — see that class's doc comment
/// for confirmation that its "safe to block synchronously on `GetAuthenticationStateAsync()`"
/// property still holds under this revalidating provider (it does — revalidation runs on its own
/// background timer, not inside that call).
/// </summary>
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // A new scope, not the one this provider itself was constructed in — ensures a fresh
        // UserManager/DbContext rather than a possibly-long-lived one, the same reasoning the
        // official template uses.
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        return await ValidateSecurityStampAsync(userManager, authenticationState.User);
    }

    private async Task<bool> ValidateSecurityStampAsync(UserManager<VlmsIdentityUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }
        else if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }
        else
        {
            var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
            var userStamp = await userManager.GetSecurityStampAsync(user);
            return principalStamp == userStamp;
        }
    }
}
