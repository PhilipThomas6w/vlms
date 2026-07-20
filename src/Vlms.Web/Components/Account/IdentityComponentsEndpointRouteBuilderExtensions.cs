using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Vlms.Infrastructure.Identity;

namespace Vlms.Web.Components.Account;

/// <summary>
/// Non-interactive endpoints Identity's own sign-out needs — adapted from the relevant part of the
/// official ASP.NET Core Identity + Blazor Web App template's
/// `Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs` (verified against
/// current Microsoft Learn guidance, CLAUDE.md "verify, don't invent").
///
/// **Scoped down, deliberately, from the full template — recorded per STATE.md's instruction not to
/// silently drop coverage:** the official template also maps external-login (`PerformExternalLogin`),
/// passkey (`PasskeyCreationOptions`/`PasskeyRequestOptions`), and personal-data-download endpoints.
/// None of those apply here — this app has no external login providers configured and no passkey/GDPR
/// personal-data-export feature was asked for in this item — so only the one endpoint every sign-in
/// mechanism needs (`/Account/Logout`) is mapped. Re-add the others here if/when those features land.
///
/// **Why an endpoint at all, rather than a `SignInManager` call from an interactive Razor component:**
/// signing out clears the auth cookie, which requires writing an HTTP response header — something an
/// already-established Blazor Server interactive circuit's SignalR connection cannot do (the same
/// reason Login/Register/the invite-accept page are static-SSR components, not interactive ones —
/// see `Login.razor`'s doc comment). The nav-menu "Log out" control is therefore a plain HTML
/// `&lt;form method="post" action="Account/Logout"&gt;`, not a Blazor event handler.
/// </summary>
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            SignInManager<VlmsIdentityUser> signInManager, [FromForm] string returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect($"~/{returnUrl}");
        });

        return endpoints;
    }
}
