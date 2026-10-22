using Microsoft.AspNetCore.Components.Authorization;

namespace Vlms.Web;

/// <summary>
/// Hands the request's authenticated <see cref="HttpContext.User"/> to the scoped
/// <see cref="AuthenticationStateProvider"/> as soon as authentication has run, so that
/// <see cref="Vlms.Infrastructure.Security.IdentityCurrentUserContext"/> resolves the caller in
/// every request scope, not only inside a rendered Razor component.
///
/// Without this, the framework sets the provider's state only when components render
/// (<see cref="IHostEnvironmentAuthenticationStateProvider"/>: "it will receive authentication state
/// from the HttpContext", learn.microsoft.com/dotnet/api/microsoft.aspnetcore.components.authorization.ihostenvironmentauthenticationstateprovider).
/// The authorization middleware and minimal-API endpoints (<see cref="LessonFileEndpoint"/>) run
/// before that, so every <c>MfaRequirementHandler</c>/<c>AnyRoleRequirement</c> evaluation threw
/// "Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component" and every
/// protected page answered 500 (found 22/10/2026, the first run of the app against PostgreSQL).
/// Inside an interactive circuit the circuit host sets the state itself; this middleware does not
/// run there.
/// </summary>
public static class AuthenticationStatePriming
{
    public static IApplicationBuilder UseAuthenticationStatePriming(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            Prime(context);
            return next(context);
        });

    public static void Prime(HttpContext context)
    {
        if (context.RequestServices.GetService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider provider)
        {
            provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(context.User)));
        }
    }
}
