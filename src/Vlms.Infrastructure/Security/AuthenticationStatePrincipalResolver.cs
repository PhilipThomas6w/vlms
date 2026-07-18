using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Resolves the current caller's <see cref="ClaimsPrincipal"/> via
/// <see cref="AuthenticationStateProvider"/> rather than <c>IHttpContextAccessor</c>.
///
/// Fixes the known gap this codebase shipped with (see openwiki/authentication-authorization.md
/// and STATE.md, commit c318bc5's checker review): Vlms.Web's Program.cs originally resolved
/// <see cref="EntraCurrentUserContext"/>'s principal from
/// <c>IHttpContextAccessor.HttpContext?.User</c>. That works for the initial HTTP request/static
/// SSR, but <c>IHttpContextAccessor.HttpContext</c> is null outside the originating request —
/// including for the whole lifetime of a Blazor Server interactive (SignalR circuit) render, since
/// the circuit isn't itself an HTTP request. The old code failed closed (an empty principal, so
/// every role check denied) rather than throwing, which is safe but silently breaks every
/// authorised user the moment a page goes interactive.
///
/// <see cref="AuthenticationStateProvider"/> (registered by ASP.NET Core's
/// <c>AddCascadingAuthenticationState()</c>, wired in Program.cs) doesn't have this limitation: for
/// a Blazor Web App using any non-Identity authentication scheme (cookie/OIDC here, via
/// Microsoft.Identity.Web), the framework captures <c>HttpContext.User</c> once — during static
/// SSR/the request that establishes the circuit — and flows it through the rest of the circuit's
/// lifetime, including every subsequent interactive render. This is the officially documented
/// replacement for IHttpContextAccessor in this exact scenario (see
/// "ASP.NET Core Blazor authentication and authorization" on Microsoft Learn, "Server-side Blazor
/// authentication").
///
/// <see cref="AuthenticationStateProvider.GetAuthenticationStateAsync"/> is blocked on
/// synchronously (<c>GetAwaiter().GetResult()</c>) here rather than awaited, because
/// <see cref="ICurrentUserContext.HasRole"/>/<see cref="ICurrentUserContext.UserId"/> must stay
/// synchronous — they are evaluated inside <see cref="VlmsDbContext"/>'s EF Core query filter
/// lambdas (adr/0004-sensitive-data-access-control.md), which cannot be made async without a much
/// larger change to how EF Core query filters work. This is safe (no deadlock, no blocking on live
/// I/O): by the time any consumer resolves <see cref="ICurrentUserContext"/> from this class, the
/// framework has already captured and stored the <see cref="AuthenticationState"/> —
/// <c>GetAuthenticationStateAsync()</c> returns an already-completed <see cref="Task"/>.
/// </summary>
public static class AuthenticationStatePrincipalResolver
{
    public static ClaimsPrincipal Resolve(AuthenticationStateProvider authenticationStateProvider)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);

        var state = authenticationStateProvider.GetAuthenticationStateAsync().GetAwaiter().GetResult();
        return state.User;
    }
}
