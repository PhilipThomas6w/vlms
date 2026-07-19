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

## Gate stage (`build/check-access-control.ps1`)

`build/verify.ps1`'s full-only `access-control (ASVS 5.0 V8)` stage re-confirms three mechanical
properties of this mechanism on every full run — zero `IgnoreQueryFilters()` call sites in `src/`,
every page `[Authorize]`-gated, and the ADR-0004 test suites at/above a floor test count — plus a
content-hash currency check on a paired human checklist
(`docs/governance/asvs-access-control-checklist.md`). See [verify-gate.md](verify-gate.md) for the
full mechanism and the chapter-numbering correction (this is ASVS 5.0 V8 Authorization, not V1 —
V1 in 5.0 is "Encoding and Sanitization").

## Tamper protection (built)

`SensitiveDataAccessLog` has `UPDATE`/`DELETE` denied at the database permission level. Two
migrations are involved: `DenyUpdateDeleteOnSensitiveDataAccessLogs` added the original DENY, and
`SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs` re-targeted it (both in
`src/Vlms.Infrastructure/Migrations/`). The current, effective statement is:

```sql
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'VlmsAppRole' AND type = 'R')
BEGIN
    DROP ROLE VlmsAppRole;
END

DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO public;
```

**Why `public`, not a dedicated role (pass-5 decision, see ADR-0004 §4).** The original migration
denied to a dedicated `VlmsAppRole` that the production principal had to be explicitly added to. A
checker review flagged that as a forgettable-provisioning gap: a role starts with zero members, so
the DENY was inert until someone remembered to add the principal — the migration could run cleanly
and the audit log still be fully mutable. The DENY is now applied to `public`, which **every**
database user belongs to and cannot be removed from (verified against Microsoft Learn:
[Database-Level Roles](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/database-level-roles)),
so it protects every current and future principal automatically — nothing to provision, nothing to
forget. `DENY` overrides all grants except for object owners and `sysadmin`
([DENY (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/deny-transact-sql)),
so the only principals that can still mutate the log are `dbo` (owner of the table) and `sysadmin` /
the Azure SQL server principal — the unavoidable DBA-level identities. The redundant `VlmsAppRole`
is dropped.

**Residual requirement (tracked in `docs/governance/raid.md` D-004):** the app must connect as a
least-privilege contained user (`db_datareader` + `db_datawriter`), never `db_owner`/object-owner/
server-admin, or it bypasses this DENY; and any future retention-purge of expired audit rows must
run as a deliberately elevated principal. `INSERT` is deliberately not denied — the
`SensitiveDataAuditInterceptor`'s own writes must keep working — and `TRUNCATE` is not denied either,
since neither the ADR nor `governance/security-compliance.md` names it.

This is raw SQL Server T-SQL and is never applied against the SQLite in-memory provider the test
suite uses — tests build schema via `Database.EnsureCreated()` from the current model, never
`Database.Migrate()`, and neither `Vlms.Web` nor `Vlms.Jobs` calls `Database.Migrate()` either
(migrations are only ever applied via an explicit `dotnet ef database update` against a real SQL
Server). `dotnet ef migrations has-pending-model-changes` confirms both migrations have no
entity/model drift — they're pure DDL. Verified via `tests/Vlms.Tests/Infrastructure/MigrationsTests.cs`,
which generates the real migration SQL in-process through EF Core's `IMigrator.GenerateScript()` (the
same mechanism `dotnet ef migrations script` uses) and asserts the effective `DENY` now targets
`public`, targets the right table, drops the superseded `VlmsAppRole`, and doesn't over-deny
`INSERT`/`TRUNCATE` — a genuine, if narrow, regression guard given `DENY` can't be exercised against
a live SQL Server from this test suite.

The 6-year retention period itself (a purge/retention job for `SensitiveDataAccessLog`) is a separate,
not-yet-built concern.

## Tests

`tests/Vlms.Tests/Infrastructure/SensitiveDataAccessControlTests.cs` — the reference pattern for testing this mechanism: real SQLite-in-memory `VlmsDbContext` via `AddDbContext` in a DI container (not a bare `new VlmsDbContext(...)`), asserting exact row counts and entity IDs for both allow and deny cases. See [testing.md](testing.md).
