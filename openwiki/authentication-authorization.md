# Authentication and authorization (as built)

Design source: `docs/adr/0001-technology-stack.md` (Entra External ID choice), `docs/adr/0002-roles-as-application-claims.md` (why roles are app-managed, not Entra groups), `docs/design/low-level-design.md` "Authorization model".

## Sign-in wiring (`src/Vlms.Web/Program.cs`)

`Microsoft.Identity.Web`'s `AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))`. `appsettings.json`'s `AzureAd` section is **placeholder-only** (no live tenant, no secrets) — there is no Entra tenant available to this build, so the OIDC handshake itself is untested; everything downstream of a successful sign-in is tested with a plain `ClaimsPrincipal`, which needs no live tenant.

An `OnTokenValidated` hook calls `UserProvisioningService.FindOrCreateAsync` on every successful sign-in.

## Provisioning (`src/Vlms.Infrastructure/Provisioning/UserProvisioningService.cs`)

Find-or-create `AppUser` by Entra object ID. **A newly created `AppUser` gets zero `UserRole` rows** — deny-by-default. Role assignment is a separate, not-yet-built, Admin-only action. Worth knowing: new-user IDs are computed as `MaxAsync(Id) + 1` (consistent with the model's application-assigned keys, see [data-access.md](data-access.md)) — the code comments this as race-prone under concurrent first sign-ins but acceptable at this system's tens-of-users scale (`docs/VISION.md`). Revisit if that scale assumption ever changes.

## `ICurrentUserContext` implementations

- `NullCurrentUserContext` — deny-by-default, for design-time/migrations tooling and as an internal lookup-context (see [access-control.md](access-control.md)). Never wire as the runtime context.
- `EntraCurrentUserContext` — the real one, resolves `UserId`/roles from `AppUser`/`UserRole` via the caller's `ClaimsPrincipal`, lazily and cached per instance. Two constructors: one takes an already-resolved `ClaimsPrincipal` directly (used by tests — no ASP.NET Core hosting needed); the other takes the `AuthenticationStateProvider` itself and is what `Program.cs` uses at runtime (see below) — it defers calling `AuthenticationStatePrincipalResolver.Resolve` until `UserId`/`HasRole` is actually *read*, not at construction.

## Resolving the caller's `ClaimsPrincipal`: `AuthenticationStateProvider`, not `IHttpContextAccessor`

`Program.cs` builds `EntraCurrentUserContext` from the `AuthenticationStateProvider` itself, which
internally resolves the `ClaimsPrincipal` via
`AuthenticationStatePrincipalResolver.Resolve(AuthenticationStateProvider)`
(`src/Vlms.Infrastructure/Security/AuthenticationStatePrincipalResolver.cs`), not
`IHttpContextAccessor.HttpContext?.User`. This closes a gap the codebase originally shipped with
(flagged by a checker review on commit c318bc5): `IHttpContextAccessor.HttpContext` is null outside
the request that established a Blazor Server interactive (SignalR circuit) render — so the old code
resolved to an empty principal (fails closed, safe, but denies every authorised user) the moment a
page went interactive. `AuthenticationStateProvider` doesn't have this limitation: ASP.NET Core's
built-in Blazor Web App wiring (`builder.Services.AddCascadingAuthenticationState()`, in
`Program.cs`) captures `HttpContext.User` once — during static SSR/the request that establishes the
circuit — and flows it through the rest of the circuit's lifetime, including every subsequent
interactive render. This is the officially documented replacement for `IHttpContextAccessor` in
this scenario ("ASP.NET Core Blazor authentication and authorization" → "Server-side Blazor
authentication", Microsoft Learn) and is exactly the pattern Microsoft's own Windows-Authentication
Blazor Web App sample uses for a non-Identity authentication scheme.

`AuthenticationStatePrincipalResolver.Resolve` blocks synchronously
(`GetAwaiter().GetResult()`) on `GetAuthenticationStateAsync()` rather than awaiting it, because
`ICurrentUserContext.HasRole`/`UserId` must stay synchronous (they run inside `VlmsDbContext`'s EF
Core query filter lambdas — adr/0004-sensitive-data-access-control.md — which can't be made async).
**This is safe only for consumers that resolve `ICurrentUserContext` inside a rendered Razor
component's DI scope** (interactive pages/policies, resource-based authorization handlers) — by
that point the framework has already captured and stored the `AuthenticationState` via ASP.NET
Core's built-in `ServerAuthenticationStateProvider`, so `GetAuthenticationStateAsync()` returns an
already-completed `Task`: no blocking on live I/O, no deadlock. It is **not** safe in general: a
checker round-trip (STATE.md's log, follow-up on commit d2adf82) found that this codebase
originally called `AuthenticationStatePrincipalResolver.Resolve` *eagerly*, inside `Program.cs`'s
`ICurrentUserContext` DI factory, before constructing `EntraCurrentUserContext`. That factory also
runs during the OIDC `OnTokenValidated` callback (`UserProvisioningService` → `VlmsDbContext` →
`ICurrentUserContext`), which is not a rendered component's DI scope — `ServerAuthenticationStateProvider`
throws `InvalidOperationException` there because no component has called `SetAuthenticationState` yet
(the test double's message string approximates this for test purposes; it is not a verbatim quote of
the framework's actual exception text — don't rely on matching it).
Every sign-in threw, and no `AppUser`/`UserRole` rows were ever created.

**The actual fix, and the property that now holds:** resolution is genuinely deferred, not merely
moved. `EntraCurrentUserContext`'s `AuthenticationStateProvider` constructor stores the provider and
only calls `AuthenticationStatePrincipalResolver.Resolve` inside its `Lazy<...>`, forced solely by a
read of `UserId`/`HasRole`. `Program.cs`'s DI factory no longer calls `Resolve` itself — it just
passes the `AuthenticationStateProvider` through. `UserProvisioningService` only touches
`AppUser`/`UserRole` directly and never reads `ICurrentUserContext.UserId`/`HasRole`, so the OIDC
path constructs `EntraCurrentUserContext` but never forces resolution — no throw, regardless of DI
scope. Component-scope consumers (which do read `UserId`/`HasRole`) still land in the safe case
described above. Verified by `AuthenticationStatePrincipalResolverTests`
(`tests/Vlms.Tests/Infrastructure/`): a `FakeAuthenticationStateProvider` resolves a role through
`EntraCurrentUserContext` with no `IHttpContextAccessor`/`HttpContext` anywhere in the object graph
(the original interactive-render gap); a `ThrowingUntilPrimedAuthenticationStateProvider` reproduces
the real `ServerAuthenticationStateProvider`'s unprimed-throw behaviour and proves both that
constructing `EntraCurrentUserContext` against it never throws, and that the real
`UserProvisioningService` → `VlmsDbContext` → `ICurrentUserContext` chain completes cleanly even
though reading `UserId`/`HasRole` on that same instance would throw (the regression fix).

Routes.razor uses `AuthorizeRouteView` (not a plain `RouteView`), which is what makes a page's
`@attribute [Authorize(Policy = "...")]` actually enforced, cascading the resulting
`Task<AuthenticationState>` down to the page — see [web.md](web.md).

## Authorization model

- **Role-based**: one ASP.NET Core policy per `Role` enum value (`RequireAdmin`, `RequireTeacher`, ...), backed by `RoleRequirement`/`RoleAuthorizationHandler`.
- **Resource-based** (`StudentAccess` policy, `StudentAccessRequirement`, resource type `Student`):
  - `ParentStudentAccessHandler` — succeeds only if the target `Student` is reachable via `StudentGuardianLink` from a `ParentGuardian` whose `AppUserId` matches the caller. A join, not a bare role check — verified per-student, not per-parent-in-general.
  - `StudentSelfAccessHandler` — succeeds only if `resource.AppUserId == callerUserId`. Null-safe both ways: an unlinked student (`AppUserId == null`) and a caller with no resolved `UserId` both correctly fail rather than accidentally matching each other.
  - `TeacherStudentAccessHandler` — role-only (Teachers see all students by design, `docs/design/low-level-design.md`), no per-resource check.

All three were adversarially checked (Opus-model checker) for the specific failure mode of "the deny path is accidentally permissive" — confirmed clean, see `STATE.md`'s log entry for that review.

## Curriculum-management policies

`RequireTeacher`/`RequireApprover` (both plain role policies, generated by the `foreach (var role in Enum.GetValues<Role>())` loop above) gate the two curriculum pages — see [curriculum.md](curriculum.md) and [web.md](web.md). `LessonProposalService` also re-checks the caller's role itself (defense in depth, not solely UI-level gating) — see curriculum.md.

## Multi-role policy: `AnyRoleRequirement`/`AnyRoleAuthorizationHandler`

The single-role machinery above (`RoleRequirement`/`RoleAuthorizationHandler`, one `RequireX` policy per `Role` value) can't express "Admin OR Teacher", needed for the guardian-links page (FR-004). Added `AnyRoleRequirement` (params `Role[]`, constructor-guarded against an empty list) and `AnyRoleAuthorizationHandler` (`src/Vlms.Infrastructure/Authorization/`) — additive, registered alongside `RoleAuthorizationHandler` in `Program.cs`, wired to a new `"RequireAdminOrTeacher"` policy. See [guardian-links.md](guardian-links.md).

The same `AnyRoleRequirement`/`AnyRoleAuthorizationHandler` machinery also backs a second policy, `"RequireAdminOrSafeguardingOfficer"` (`Program.cs`), gating the consent/DBS management pages — matches the `VlmsDbContext` query filter on `DbsCheck`/`ConsentSensitiveDetails` exactly. See [safeguarding-consent.md](safeguarding-consent.md).
