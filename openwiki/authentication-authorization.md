# Authentication and authorization (as built)

Design source: `docs/adr/0001-technology-stack.md` (Entra External ID choice), `docs/adr/0002-roles-as-application-claims.md` (why roles are app-managed, not Entra groups), `docs/design/low-level-design.md` "Authorization model".

## Sign-in wiring (`src/Vlms.Web/Program.cs`)

`Microsoft.Identity.Web`'s `AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))`. `appsettings.json`'s `AzureAd` section is **placeholder-only** (no live tenant, no secrets) — there is no Entra tenant available to this build, so the OIDC handshake itself is untested; everything downstream of a successful sign-in is tested with a plain `ClaimsPrincipal`, which needs no live tenant.

An `OnTokenValidated` hook calls `UserProvisioningService.FindOrCreateAsync` on every successful sign-in.

## Provisioning (`src/Vlms.Infrastructure/Provisioning/UserProvisioningService.cs`)

Find-or-create `AppUser` by Entra object ID. **A newly created `AppUser` gets zero `UserRole` rows** — deny-by-default. Role assignment is a separate, not-yet-built, Admin-only action. Worth knowing: new-user IDs are computed as `MaxAsync(Id) + 1` (consistent with the model's application-assigned keys, see [data-access.md](data-access.md)) — the code comments this as race-prone under concurrent first sign-ins but acceptable at this system's tens-of-users scale (`docs/VISION.md`). Revisit if that scale assumption ever changes.

## `ICurrentUserContext` implementations

- `NullCurrentUserContext` — deny-by-default, for design-time/migrations tooling and as an internal lookup-context (see [access-control.md](access-control.md)). Never wire as the runtime context.
- `EntraCurrentUserContext` — the real one, resolves `UserId`/roles from `AppUser`/`UserRole` via the caller's `ClaimsPrincipal`, lazily and cached per request-scoped instance. Takes the `ClaimsPrincipal` directly (not `IHttpContextAccessor`) for testability — `Program.cs` is where `IHttpContextAccessor` gets consulted, once, to build the principal that's handed in.

## Authorization model

- **Role-based**: one ASP.NET Core policy per `Role` enum value (`RequireAdmin`, `RequireTeacher`, ...), backed by `RoleRequirement`/`RoleAuthorizationHandler`.
- **Resource-based** (`StudentAccess` policy, `StudentAccessRequirement`, resource type `Student`):
  - `ParentStudentAccessHandler` — succeeds only if the target `Student` is reachable via `StudentGuardianLink` from a `ParentGuardian` whose `AppUserId` matches the caller. A join, not a bare role check — verified per-student, not per-parent-in-general.
  - `StudentSelfAccessHandler` — succeeds only if `resource.AppUserId == callerUserId`. Null-safe both ways: an unlinked student (`AppUserId == null`) and a caller with no resolved `UserId` both correctly fail rather than accidentally matching each other.
  - `TeacherStudentAccessHandler` — role-only (Teachers see all students by design, `docs/design/low-level-design.md`), no per-resource check.

All three were adversarially checked (Opus-model checker) for the specific failure mode of "the deny path is accidentally permissive" — confirmed clean, see `STATE.md`'s log entry for that review.

## Known gap: interactive Blazor render mode

`Program.cs` resolves the current `ClaimsPrincipal` from `IHttpContextAccessor.HttpContext?.User`. In Blazor Server's **interactive** (SignalR circuit) render mode, `IHttpContextAccessor.HttpContext` is null outside the initial HTTP request — so `EntraCurrentUserContext` will resolve to an empty principal during interactive rendering, and every role check will fail. This **fails closed** (safe for the safeguarding data this protects — no permissive path), but it is a functional bug waiting to happen the moment interactive UI is built against these policies. Tracked as a note on `STATE.md` Next item 1 (curriculum management, the first UI work). Fix by surfacing the authenticated user via `AuthenticationStateProvider`/persisted circuit state instead of `IHttpContextAccessor` when building that item — don't rediscover this the hard way.
