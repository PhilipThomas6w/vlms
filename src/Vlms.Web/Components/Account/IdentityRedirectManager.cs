using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace Vlms.Web.Components.Account;

/// <summary>
/// Adapted from the official ASP.NET Core Identity + Blazor Web App template
/// (`dotnet new blazor -au Individual`, `Components/Account/IdentityRedirectManager.cs`) — verified
/// against current Microsoft Learn guidance rather than hand-rolled (CLAUDE.md "verify, don't
/// invent"; STATE.md "Identity" item). Open-redirect-safe navigation for the Account pages: relative
/// URIs are used as-is, and a caller-supplied absolute URI is collapsed to its base-relative form
/// before navigating, so a crafted "returnUrl" query value can never redirect off-site.
/// </summary>
internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    public void RedirectTo(string? uri)
    {
        uri ??= "";

        // Prevent open redirects.
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }

        // Vlms.Web.csproj sets <BlazorDisableThrowNavigationException>, so NavigateTo no longer
        // throws during static SSR and the code after a call keeps running. The template's
        // InvalidOperationException and DoesNotReturn attributes are removed accordingly, per
        // "Migrate from ASP.NET Core in .NET 9 to .NET 10" (learn.microsoft.com/aspnet/core/migration/90-to-100,
        // "When navigation errors are disabled in a Blazor Web App with Individual Accounts"). Left in,
        // that throw turned every sign-in into a 500 (found 22/10/2026). Callers must return after
        // redirecting.
        navigationManager.NavigateTo(uri);
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context) =>
        RedirectToWithStatus(CurrentPath, message, context);
}
