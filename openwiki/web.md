# Vlms.Web

Blazor Web App, **Server** interactivity (not Auto/WebAssembly — see [architecture.md](architecture.md)), scaffolded from `dotnet new blazor --interactivity Server --auth None --empty`.

## Current state

`Program.cs` has the DI wiring (`VlmsDbContext`, `ICurrentUserContext`, authorization policies, `LessonProposalService`, `GuardianLinkService`, `StudentRegistrationService`, `ParentDashboardService`, Entra sign-in, cascading authentication state — see [authentication-authorization.md](authentication-authorization.md)). `Components/Pages/` holds the template's `Error.razor`/`NotFound.razor` plus VLMS UI: `Home.razor` (role-gated links via `AuthorizeView`), `Components/Pages/Curriculum/` (`TeacherProposals.razor`, `ApproverProposals.razor` — see [curriculum.md](curriculum.md)), `Components/Pages/Guardianship/GuardianLinks.razor` (`/guardianship/links`, gated `RequireAdminOrTeacher` — see [guardian-links.md](guardian-links.md)), `Components/Pages/Registration/RegisterStudent.razor` (`/registration/students`, gated `RequireAdminOrTeacher` — see [student-registration.md](student-registration.md)), and `Components/Pages/Parent/ParentDashboard.razor` (`/parent/dashboard`, gated `RequireParent` — see [parent-dashboard.md](parent-dashboard.md)). `INotificationService`/`ConsentExpiryNotifier` are **not** wired into this project's `Program.cs` — nothing in `Vlms.Web` sends notifications; that only happens from the `Vlms.Jobs` WebJob host (see [notifications.md](notifications.md)). `Components/Routes.razor` uses `AuthorizeRouteView`, not a plain `RouteView` — see below.

## `appsettings.json`

`AzureAd` section is placeholder-only — `PLACEHOLDER-*` values, explicit comment pointing at Key Vault for real secrets. `ConnectionStrings:VlmsDatabase` is a local/dev connection string, not real Azure SQL credentials. Do not commit real values here; this file is meant to stay safe to commit as-is.

## Page-level authorization: `AuthorizeRouteView` + `[Authorize(Policy = "...")]`

`Routes.razor`'s `<Router>` uses `<AuthorizeRouteView>` (not `<RouteView>`), with a `<NotAuthorized>`
fragment. This is what makes a page's `@attribute [Authorize(Policy = "RequireTeacher")]` (or any
policy from `Program.cs`'s per-`Role` policy set) actually enforced — a plain `RouteView` ignores
`[Authorize]` attributes entirely. `AuthorizeRouteView` also supplies the
`Task<AuthenticationState>` cascading parameter that `AuthorizeView`/`[Authorize]` need, sourced
from `builder.Services.AddCascadingAuthenticationState()` in `Program.cs`.

## PWA manifest/service worker (installability, adr/0001-technology-stack.md)

`adr/0001-technology-stack.md` names the client as "Blazor Web App (.NET), responsive,
**PWA-installable**" and gives no further detail — no offline/caching behaviour, no icon spec. That
one line is the entire brief this increment builds against; `docs/design/low-level-design.md` says
nothing about PWA at all.

**Scoping judgement call, checked against Microsoft Learn rather than assumed:** Microsoft's own
[Blazor PWA documentation](https://learn.microsoft.com/aspnet/core/blazor/progressive-web-app/) is
written entirely for **Blazor WebAssembly** — its offline-execution model depends on a build-time
`service-worker-assets.js` manifest of every .NET assembly/wasm file the app needs to run
disconnected, because a WASM app's whole UI executes in the browser. None of that applies here:
`Vlms.Web` is Server-interactive (see "Current state" above) — the UI is rendered server-side and
kept live over a SignalR circuit, so there is no "run the app offline" scenario to build; without a
live connection to the server there is no app. This increment therefore builds only the two things
"PWA-installable" can actually mean for a Server-interactive app:

1. **Installability** — `wwwroot/manifest.json` (name/short_name/start_url/display/theme_color/
   background_color/icons) linked from `Components/App.razor` via `<link rel="manifest">`, plus a
   registered service worker (a standard installability signal, alongside the manifest).
2. **App-shell asset caching for faster repeat loads** — `wwwroot/service-worker.js` is a
   hand-written, minimal service worker (not the WASM template's generated one, which doesn't apply
   here): cache-first, populate-as-you-go caching of same-origin static assets whose
   `request.destination` is `style`/`script`/`image`/`font`/`manifest`. It never intercepts
   navigation requests (`request.mode === 'navigate'`) — the server-rendered HTML document is
   dynamic per-request (auth state, antiforgery tokens) and must never be served stale from a
   cache — and it doesn't need to explicitly exclude the SignalR circuit, since a WebSocket upgrade
   never dispatches a `fetch` event in the first place.

Registration is the standard web-platform pattern (`navigator.serviceWorker.register('service-worker.js')`
in a `<script>` after `App.razor`'s `blazor.web.js` tag) — this part is not Blazor-specific, so no
template precedent was needed beyond confirming the API shape.

**Placeholder-icon gap (documented, same pattern as `AzureBlobStorage`/`AzureAd`/Azure Communication
Services before their live resources existed):** no app icon/logo asset exists anywhere in this
repo. `wwwroot/icons/icon.svg` is a minimal placeholder (a slate rounded square with "VL") referenced
from `manifest.json` with `"sizes": "any"` so one vector file covers whatever raster size a browser
asks for, rather than fabricating a multi-size PNG icon set for a logo that doesn't exist yet. A real
branded icon (192×192/512×512 PNG, plus a maskable variant, and an `apple-touch-icon` for iOS —
Safari doesn't honour SVG there) is a design/branding decision outside this increment's scope; only
`manifest.json`'s `icons` array needs to change once one exists.

**Tested:** `tests/Vlms.Tests/Web/PwaAssetsTests.cs` — `manifest.json` parses as valid JSON with the
fields a browser needs, every icon it references exists on disk, `service-worker.js` exists and is
non-empty, and `App.razor` actually links the manifest and registers the service worker. This is
deliberately the extent of what's unit-testable here: there is no server-side logic to test (static
assets + a registration script), and runtime installability/caching behaviour needs a real browser,
not `dotnet test`.

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
