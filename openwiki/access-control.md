# Sensitive-data access control (ADR-0004)

Read `docs/adr/0004-sensitive-data-access-control.md` first — it went through 4 design-review rounds because two earlier mechanisms were tried and found broken. This page is what actually got built, and the two footguns to know about before touching this code.

## The mechanism, as built

1. **Whole-entity query filters**, in `src/Vlms.Infrastructure/VlmsDbContext.cs` `OnModelCreating`:
   ```csharp
   e.HasQueryFilter(x => _currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer));
   ```
   applied to `ConsentSensitiveDetails` and `DbsCheck`. `ConsentRecord` (the non-sensitive sibling holding `Status`/`ExpiryDate`) has **no filter** — Teacher/Approver can read it. Do not add a filter to `ConsentRecord` or move sensitive fields back onto it; that's the exact mistake ADR-0004 exists to document and fix.

2. **Read-audit logging**, `src/Vlms.Infrastructure/Auditing/SensitiveDataAuditInterceptor.cs`, an `IMaterializationInterceptor` (not `IDbCommandInterceptor` — that was tried and rejected, see the ADR). `InitializedInstance` fires once per row EF materializes, so a multi-row read of `DbsCheck` writes one log row per `DbsCheck`, not one for the whole query.

## Two things that look like bugs but are deliberate

- **The interceptor opens a second `VlmsDbContext` to write the audit row**, rather than adding it to the context that's still enumerating the query being audited. This is because EF Core's reentrancy guard rejects a second operation on the same context instance mid-enumeration — not a workaround for a mistake, a structural requirement of writing from inside a materialization callback. See the class doc comment for the full explanation, confirmed correct by an adversarial Opus-model checker review.
- **`EntraCurrentUserContext` also opens its own short-lived `VlmsDbContext`** to resolve `AppUser`/`UserRole` (in `src/Vlms.Infrastructure/Security/EntraCurrentUserContext.cs`), rather than depending on the DI-resolved one — because it is itself a constructor dependency *of* `VlmsDbContext`, so depending on the DI-resolved instance would be circular. It hands that lookup context `NullCurrentUserContext.Instance` (safe: `AppUser`/`UserRole` carry no sensitive-data filter).

## `NullCurrentUserContext` (`src/Vlms.Infrastructure/Security/NullCurrentUserContext.cs`)

Deny-by-default (`HasRole` always false, `UserId` always null). Two legitimate uses: `VlmsDbContextFactory` (EF Core design-time/migrations tooling has no request context), and as the lookup-context argument inside `EntraCurrentUserContext.Resolve` (above). **Never** wire this as the runtime `ICurrentUserContext` in `Vlms.Web` — that would make every sensitive-data query silently return nothing for every user, which fails safe but breaks the app. `Program.cs` currently wires `EntraCurrentUserContext` correctly; if that ever changes, check why deliberately.

## `SystemCurrentUserContext` (`src/Vlms.Infrastructure/Security/SystemCurrentUserContext.cs`)

A third `ICurrentUserContext` implementation, added for the `ConsentExpiryJob` WebJob (see [safeguarding-consent.md](safeguarding-consent.md)) — a scheduled background job has no signed-in human caller, but it must legitimately read `DbsCheck` through the same filtered/audited path everything else uses (never `IgnoreQueryFilters()`). Unlike `NullCurrentUserContext`, it is not deny-by-default: `HasRole` returns true for exactly `Role.Admin` and `Role.SafeguardingOfficer` (precisely what the query filter above checks for) and false for everything else — narrowly scoped to what the job needs, not a general bypass. `UserId` is still null (no human to attribute the read to), which both `SensitiveDataAccessLog.UserId` and `ICurrentUserContext.UserId`'s own doc comment already model as a valid case. Only ever used by the `Vlms.Jobs` host — never wired into `Vlms.Web`.

## Retention and tamper protection (named in the ADR, not yet built)

`SensitiveDataAccessLog` is meant to be retained 6 years and tamper-protected at the database permission level (`DENY UPDATE`/`DELETE` for the app's SQL principal). Neither is in the migrations yet — tracked as `STATE.md` Next item 9 (added after the first checker review flagged it as at risk of being forgotten).

## Tests

`tests/Vlms.Tests/Infrastructure/SensitiveDataAccessControlTests.cs` — the reference pattern for testing this mechanism: real SQLite-in-memory `VlmsDbContext` via `AddDbContext` in a DI container (not a bare `new VlmsDbContext(...)`), asserting exact row counts and entity IDs for both allow and deny cases. See [testing.md](testing.md).
