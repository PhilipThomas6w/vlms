using System.Diagnostics.CodeAnalysis;
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

    [DoesNotReturn]
    public void RedirectTo(string? uri)
    {
        uri ??= "";

        // Prevent open redirects.
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }

        // During static rendering, NavigateTo throws a NavigationException which is handled by the
        // framework as a redirect. So as long as this is called from a statically rendered
        // Identity component, the InvalidOperationException is never thrown.
        navigationManager.NavigateTo(uri);
        throw new InvalidOperationException($"{nameof(IdentityRedirectManager)} can only be used during static rendering.");
    }

    [DoesNotReturn]
    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    [DoesNotReturn]
    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    [DoesNotReturn]
    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    [DoesNotReturn]
    public void RedirectToCurrentPageWithStatus(string message, HttpContext context) =>
        RedirectToWithStatus(CurrentPath, message, context);
}
