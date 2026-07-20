# Sensitive-data access control (ADR-0004)

Read `docs/adr/0004-sensitive-data-access-control.md` first — it went through 4 design-review rounds because two earlier mechanisms were tried and found broken. This page is what actually got built, and the two footguns to know about before touching this code.

## The mechanism, as built

1. **Whole-entity query filters**, in `src/Vlms.Infrastructure/VlmsDbContext.cs` `OnModelCreating`:
   ```csharp
   e.HasQueryFilter(x =>
       (_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
       && _currentUser.HasMfaEnabled);
   ```
   applied to `ConsentSensitiveDetails` and `DbsCheck`. **The `&& HasMfaEnabled` conjunct is new** (STATE.md "Identity" item 4, adr/0004 §"MFA-gated roles" pass 7) — a caller must hold Admin/SafeguardingOfficer *and* have TOTP MFA enabled on their Identity credential row before either entity is visible at all; see [authentication-authorization.md](authentication-authorization.md)'s MFA section for the full "enforced for these two roles, offered to everyone" design and why this data-layer check exists alongside, not instead of, the page-level `MfaRequirement` policy. `ConsentRecord` (the non-sensitive sibling holding `Status`/`ExpiryDate`) has **no filter** — Teacher/Approver can read it. Do not add a filter to `ConsentRecord` or move sensitive fields back onto it; that's the exact mistake ADR-0004 exists to document and fix.

2. **Read-audit logging**, `src/Vlms.Infrastructure/Auditing/SensitiveDataAuditInterceptor.cs`, an `IMaterializationInterceptor` (not `IDbCommandInterceptor` — that was tried and rejected, see the ADR). `InitializedInstance` fires once per row EF materializes, so a multi-row read of `DbsCheck` writes one log row per `DbsCheck`, not one for the whole query.

## Two things that look like bugs but are deliberate

- **The interceptor opens a second `VlmsDbContext` to write the audit row**, rather than adding it to the context that's still enumerating the query being audited. This is because EF Core's reentrancy guard rejects a second operation on the same context instance mid-enumeration — not a workaround for a mistake, a structural requirement of writing from inside a materialization callback. See the class doc comment for the full explanation, confirmed correct by an adversarial Opus-model checker review.
- **`IdentityCurrentUserContext` also opens its own short-lived `VlmsDbContext`** to resolve `AppUser`/`UserRole`/`VlmsIdentityUser.TwoFactorEnabled` (in `src/Vlms.Infrastructure/Security/IdentityCurrentUserContext.cs`, the Identity-era replacement for the Entra-era `EntraCurrentUserContext`), rather than depending on the DI-resolved one — because it is itself a constructor dependency *of* `VlmsDbContext`, so depending on the DI-resolved instance would be circular. It hands that lookup context `NullCurrentUserContext.Instance` (safe: `AppUser`/`UserRole`/`VlmsIdentityUser` carry no sensitive-data filter).

## `NullCurrentUserContext` (`src/Vlms.Infrastructure/Security/NullCurrentUserContext.cs`)

Deny-by-default (`HasRole` always false, `UserId` always null, `HasMfaEnabled` always false). Two legitimate uses: `VlmsDbContextFactory` (EF Core design-time/migrations tooling has no request context), and as the lookup-context argument inside `IdentityCurrentUserContext.Resolve` (above). **Never** wire this as the runtime `ICurrentUserContext` in `Vlms.Web` — that would make every sensitive-data query silently return nothing for every user, which fails safe but breaks the app. `Program.cs` currently wires `IdentityCurrentUserContext` correctly; if that ever changes, check why deliberately.

## `SystemCurrentUserContext` (`src/Vlms.Infrastructure/Security/SystemCurrentUserContext.cs`)

A third `ICurrentUserContext` implementation, added for the `ConsentExpiryJob` WebJob (see [safeguarding-consent.md](safeguarding-consent.md)) — a scheduled background job has no signed-in human caller, but it must legitimately read `DbsCheck` through the same filtered/audited path everything else uses (never `IgnoreQueryFilters()`). Unlike `NullCurrentUserContext`, it is not deny-by-default: `HasRole` returns true for exactly `Role.Admin` and `Role.SafeguardingOfficer` (precisely what the query filter above checks for) and false for everything else — narrowly scoped to what the job needs, not a general bypass. `HasMfaEnabled` is hardcoded `true` (STATE.md "Identity" item 4 — MFA enforcement gates *interactive sign-in by a human*, which this trusted system principal is not, so it must not be denied by the query filter's new `&& HasMfaEnabled` conjunct above). `UserId` is still null (no human to attribute the read to), which both `SensitiveDataAccessLog.UserId` and `ICurrentUserContext.UserId`'s own doc comment already model as a valid case. Only ever used by the `Vlms.Jobs` host — never wired into `Vlms.Web`.

## Gate stage (`build/check-access-control.ps1`)

`build/verify.ps1`'s full-only `access-control (ASVS 5.0 V8)` stage re-confirms three mechanical
properties of this mechanism on every full run — zero `IgnoreQueryFilters()` call sites in `src/`,
every page `[Authorize]`-gated, and the ADR-0004 test suites at/above a floor test count — plus a
content-hash currency check on a paired human checklist
(`docs/governance/asvs-access-control-checklist.md`). See [verify-gate.md](verify-gate.md) for the
full mechanism and the chapter-numbering correction (this is ASVS 5.0 V8 Authorization, not V1 —
V1 in 5.0 is "Encoding and Sanitization").

## Tamper protection (built — ported to PostgreSQL, adr/0005)

`SensitiveDataAccessLog`'s table (`sensitive_data_access_logs`, snake_case per the naming
convention — [data-access.md](data-access.md)) has `UPDATE`/`DELETE` revoked from `PUBLIC` via a
single migration, `RevokeUpdateDeleteOnSensitiveDataAccessLog`
(`src/Vlms.Infrastructure/Migrations/`) — the one-migration net effect of the two now-deleted SQL
Server DENY migrations (`DenyUpdateDeleteOnSensitiveDataAccessLogs` /
`SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs`), whose history and rationale remain
recorded in ADR-0004 §4/Alternatives. The statement:

```sql
REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC;
```

**This is *not* semantically equivalent to SQL Server's `DENY ... TO public` — checked against
current PostgreSQL documentation rather than assumed, and the gap is disclosed here rather than
implied away (per the migration's own, more detailed doc comment).** Three verified facts, not one:

1. SQL Server's `DENY` **overrides** every other grant a principal holds ("DENY takes precedence
   over all permissions" — [DENY (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/deny-transact-sql)).
   PostgreSQL's privilege model has no override concept: `REVOKE ... FROM PUBLIC` removes only the
   privilege a role held *via* `PUBLIC`. A role holding `UPDATE`/`DELETE` some other way — an
   explicit direct `GRANT`, or by owning the table — is completely unaffected
   ("revoking SELECT privilege from PUBLIC does not necessarily mean that all roles have lost
   SELECT privilege on the object: those who have it granted directly ... will still have it" —
   [REVOKE](https://www.postgresql.org/docs/current/sql-revoke.html)). It's also not persistent the
   way `DENY` is: a later `GRANT ... TO PUBLIC` would silently restore what this `REVOKE` removed.
2. PostgreSQL grants **no default privileges to `PUBLIC` on tables at all**
   ("No privileges are granted to PUBLIC by default on tables ..." —
   [Privileges](https://www.postgresql.org/docs/current/ddl-priv.html)) — unlike SQL Server, where
   `public`'s membership is what made the original DENY meaningful on day one. On a freshly created
   table this `REVOKE` has nothing to revoke; it's defensive against a future accidental
   `GRANT ... TO PUBLIC`, not an active restriction from the start.
3. Consequently, **who can still `UPDATE`/`DELETE` this table is a strictly larger set than "the
   owner and a superuser"** — the analogy this ADR originally expected (mirroring SQL Server's
   `dbo`/`sysadmin`). It's *any* role ever explicitly granted `UPDATE`/`DELETE` directly, or that
   owns the table. The load-bearing control is therefore entirely operational, more so than under
   SQL Server: the app's connecting role must never be this table's owner and must never be granted
   `UPDATE`/`DELETE` on it directly (only `SELECT`/`INSERT`, which
   `SensitiveDataAuditInterceptor`'s own writes need) — tracked in `docs/governance/raid.md` D-004.

**A true `DENY`-equivalent does exist in PostgreSQL — Row-Level Security — and was deliberately not
built here.** `ALTER TABLE ... FORCE ROW LEVEL SECURITY` plus `CREATE POLICY ... FOR UPDATE
USING (false)` / `FOR DELETE USING (false)` blocks `UPDATE`/`DELETE` for every role including the
table owner, with only a superuser (or a role with `BYPASSRLS`) exempt — verified against
[Row Security Policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html). Not built in
this migration because enabling row security requires an explicit policy for *every* command that
must keep working, or that command is default-denied too ("if row-level security is enabled for a
table, but no applicable policies exist, a 'default deny' policy is assumed" — same page) — getting
that wrong would silently block the audit interceptor's own `INSERT` path, the single most
safety-critical write in this codebase, inside what this item scoped as a same-provider *port*, not
a mechanism redesign. Tracked as a new, separate, deliberately higher-scrutiny follow-up item
(STATE.md Next) rather than attempted here.

**Residual requirement (tracked in `docs/governance/raid.md` D-004, generalised for PostgreSQL):**
the app must connect as a least-privilege role — able to `SELECT`/`INSERT` on
`sensitive_data_access_logs` but never granted `UPDATE`/`DELETE`, and never the table owner or a
superuser/`BYPASSRLS` role; any future retention-purge of expired audit rows must run as a
deliberately elevated role. `INSERT` is deliberately not revoked — the
`SensitiveDataAuditInterceptor`'s own writes must keep working — and `TRUNCATE` is not revoked
either, since neither the ADR nor `governance/security-compliance.md` names it.

This is raw PostgreSQL-specific SQL and is never applied against the SQLite in-memory provider the
test suite uses — tests build schema via `Database.EnsureCreated()` from the current model, never
`Database.Migrate()`, and neither `Vlms.Web` nor `Vlms.Jobs` calls `Database.Migrate()` either
(migrations are only ever applied via an explicit `dotnet ef database update` against a real
PostgreSQL server). `dotnet ef migrations has-pending-model-changes` confirms the migration has no
entity/model drift — it's pure DDL. Verified via `tests/Vlms.Tests/Infrastructure/MigrationsTests.cs`,
which generates the real migration SQL in-process through EF Core's `IMigrator.GenerateScript()` (the
same mechanism `dotnet ef migrations script` uses, now against the Npgsql-configured context) and
asserts the exact `REVOKE` statement is present, targets the right table, is applied after the table
is created, and doesn't over-revoke `INSERT`/`TRUNCATE` — a genuine, if narrow, regression guard given
this can't be exercised against a live PostgreSQL server from this test suite. It proves the literal,
disclosed-as-partial statement is present — not that it achieves `DENY`'s override semantics, which
it verifiably doesn't (above).

The 6-year retention period itself (a purge/retention job for `SensitiveDataAccessLog`) is a separate,
not-yet-built concern.

## Tests

`tests/Vlms.Tests/Infrastructure/SensitiveDataAccessControlTests.cs` — the reference pattern for testing this mechanism: real SQLite-in-memory `VlmsDbContext` via `AddDbContext` in a DI container (not a bare `new VlmsDbContext(...)`), asserting exact row counts and entity IDs for both allow and deny cases. Its MFA-gating cases (added with STATE.md's "Identity" item) use `FakeCurrentUserContext`'s `hasMfaEnabled` constructor parameter — defaults to `true` so every pre-existing test keeps testing exactly the property it was written for, with explicit `false` only in the tests that specifically exercise MFA denial. See [testing.md](testing.md).
