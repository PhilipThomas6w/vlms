# Vlms.Web

Blazor Web App, **Server** interactivity (not Auto/WebAssembly — see [architecture.md](architecture.md)), scaffolded from `dotnet new blazor --interactivity Server --auth None --empty`.

## Current state

`Program.cs` has the DI wiring (`VlmsDbContext`, `ICurrentUserContext`, authorization policies, `LessonProposalService`, Entra sign-in, cascading authentication state — see [authentication-authorization.md](authentication-authorization.md)). `Components/Pages/` holds the template's `Error.razor`/`NotFound.razor` plus VLMS UI: `Home.razor` (role-gated links via `AuthorizeView`) and `Components/Pages/Curriculum/` (`TeacherProposals.razor`, `ApproverProposals.razor` — see [curriculum.md](curriculum.md)). `Components/Routes.razor` uses `AuthorizeRouteView`, not a plain `RouteView` — see below.

## `appsettings.json`

`AzureAd` section is placeholder-only — `PLACEHOLDER-*` values, explicit comment pointing at Key Vault for real secrets. `ConnectionStrings:VlmsDatabase` is a local/dev connection string, not real Azure SQL credentials. Do not commit real values here; this file is meant to stay safe to commit as-is.

## Page-level authorization: `AuthorizeRouteView` + `[Authorize(Policy = "...")]`

`Routes.razor`'s `<Router>` uses `<AuthorizeRouteView>` (not `<RouteView>`), with a `<NotAuthorized>`
fragment. This is what makes a page's `@attribute [Authorize(Policy = "RequireTeacher")]` (or any
policy from `Program.cs`'s per-`Role` policy set) actually enforced — a plain `RouteView` ignores
`[Authorize]` attributes entirely. `AuthorizeRouteView` also supplies the
`Task<AuthenticationState>` cascading parameter that `AuthorizeView`/`[Authorize]` need, sourced
from `builder.Services.AddCascadingAuthenticationState()` in `Program.cs`.

## Resolving the caller in interactive components (fixed, then a round-trip fix on top)

This codebase originally resolved the current-request `ClaimsPrincipal` via
`IHttpContextAccessor.HttpContext?.User`, which silently denied every role check once a component
ran in Blazor Server's interactive (SignalR circuit) render mode (`IHttpContextAccessor.HttpContext`
is null there). Fixed by resolving via `AuthenticationStateProvider` instead — see
[authentication-authorization.md](authentication-authorization.md)'s "Resolving the caller's
ClaimsPrincipal" section for the full mechanism. For component-scope consumers — any interactive
page/policy/authorization handler that reads `ICurrentUserContext` — this works the same in static
SSR and interactive render, no special handling needed.

That first fix itself regressed sign-in (a checker round-trip found it): `Program.cs`'s
`ICurrentUserContext` DI factory called `AuthenticationStatePrincipalResolver.Resolve` *eagerly*,
and that same factory runs during the OIDC `OnTokenValidated` callback
(`UserProvisioningService` → `VlmsDbContext` → `ICurrentUserContext`) — not a rendered component's
DI scope, where the real `ServerAuthenticationStateProvider` throws instead of returning a captured
state. Fixed by making resolution genuinely lazy: `EntraCurrentUserContext` now takes the
`AuthenticationStateProvider` itself and only resolves the principal on first read of
`UserId`/`HasRole`, so construction (and anything that constructs but never reads it, like the OIDC
provisioning path) never triggers the throw. See
[authentication-authorization.md](authentication-authorization.md) for the full mechanism and the
distinction between "safe for component-scope consumers" and "safe because resolution is deferred
until first read, which the OIDC path never triggers."
