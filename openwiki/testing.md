# Testing conventions

`tests/Vlms.Tests/`, xUnit. `dotnet test` is a `build/verify.ps1` gate stage — every test that exists must actually pass, not be skipped or stubbed to green.

## The SQLite-in-memory pattern (the reference: `Infrastructure/SensitiveDataAccessControlTests.cs`)

This repo's rule for testing anything touching `VlmsDbContext`: use a **real SQLite in-memory database**, not the EF Core `InMemory` provider — `InMemory` doesn't enforce relational constraints or faithfully support query-filter semantics, and this codebase's query filters are exactly the thing that needs faithful testing (see [access-control.md](access-control.md)).

Two non-obvious requirements, both load-bearing (discovered the hard way during the access-control work, per the maker's session):

1. **Resolve `VlmsDbContext` via a real DI container** (`AddDbContext` + `ServiceProvider`), not `new VlmsDbContext(...)` directly. EF Core only wires a context's internal service provider back to the application's `IServiceProvider` when the context is constructed through DI — and `materializationData.Context.GetService<T>()` inside `SensitiveDataAuditInterceptor` depends on that wiring. A bare `new VlmsDbContext(...)` in a test would silently fail to exercise the same resolution path production uses.
2. **Give every context the connection string, not a shared `SqliteConnection` instance.** EF's SQLite provider re-registers SQL functions on whichever physical connection it wraps, which SQLite refuses while that connection has an active statement — a real conflict when the audit interceptor opens a second context mid-read (see [access-control.md](access-control.md)). Use a named, shared-cache in-memory database (`Data Source=file:<name>;Mode=Memory;Cache=Shared`) kept alive by one long-lived anchor connection, so each context opens its own physical connection but sees the same data.

If a new test needs a `VlmsDbContext`, copy this pattern rather than reinventing it — don't drop back to `InMemory` or a shared connection instance for convenience. Further examples beyond the reference file: `IdentityCurrentUserContextTests`, `AppUserProvisioningServiceTests`, `GuardianInviteServiceTests`, `AuthenticationStatePrincipalResolverTests`, `LessonSuggestionServiceTests` (all `tests/Vlms.Tests/Infrastructure/`).

## `FakeCurrentUserContext` (`Infrastructure/FakeCurrentUserContext.cs`)

A minimal `ICurrentUserContext` test double: `new FakeCurrentUserContext(userId, params Role[] roles)`, or `new FakeCurrentUserContext(userId, hasMfaEnabled, params Role[] roles)` when a test needs to control MFA status explicitly (STATE.md "Identity" item 4 — the `hasMfaEnabled`-less overload defaults to `true`, so every test written before MFA enforcement existed keeps testing exactly the property it was written for). Used to simulate "the caller" in tests — it only supplies role/identity/MFA data; the actual authorization/query-filter logic under test is always the real production code, never mocked.

## `TestIdentityServices` (`Infrastructure/TestIdentityServices.cs`)

A small DI-registration helper (STATE.md "Identity" item) that wires a real `UserManager<VlmsIdentityUser>`/Identity store against the test's SQLite-in-memory `VlmsDbContext`, an `EphemeralDataProtectionProvider` (in-memory keys, no disk I/O), and a `FakeNotificationService` (captures invite emails instead of sending them). Needed more broadly than just `AppUserProvisioningServiceTests`/`GuardianInviteServiceTests`: once `GuardianLinkService` started issuing an invite from inside its two entry points, essentially every pre-existing `GuardianLinkServiceTests`/`StudentRegistrationServiceTests` case (their seed guardian never has `AppUserId` set) now exercises the same DI chain, so this factory wires the real shape (matching `Vlms.Web`'s `Program.cs`) rather than each test file inventing its own shortcut. `UserManager<TUser>` is not an interface, so faking it convincingly would be more work than wiring the real one against a test database. `BuildServiceProvider` also takes an optional `publicBaseUrl` parameter (default `"https://test.example"`; pass `null` to omit `App:PublicBaseUrl` from the in-memory configuration entirely) — added by the `GuardianLinkService` atomicity checker-FAIL round-trip fix (see [guardian-links.md](guardian-links.md)'s "Atomicity" section) so `GuardianLinkServiceTests` can reproduce `GuardianInviteService.SendInviteEmailAsync`'s real "not configured" failure mode without adding an artificial fault-injection knob to `FakeNotificationService`.

## `ListLogger<T>` (`Infrastructure/ListLogger.cs`)

A minimal `ILogger<T>` test double, same spirit as `FakeCurrentUserContext`/`InMemoryBlobStorage`: records every logged `(LogLevel, formatted message)` pair to a list so a test can assert on log severity (e.g. `ConsentExpiryJob`'s Error-vs-Warning escalation distinction, see [safeguarding-consent.md](safeguarding-consent.md)) without pulling in a mocking library.

## Test-provider strategy after the PostgreSQL pivot (adr/0005) — decided explicitly, not assumed

adr/0005-hosting-pivot-self-hosted-vps-postgresql.md moved the production provider from Azure SQL
Database to self-hosted PostgreSQL and flagged the test-provider question as open ("whether to keep
[SQLite] as-is or move to a real PostgreSQL instance in tests ... is an open implementation
decision"). **Decision: the SQLite-in-memory-via-DI pattern above stays the default for the whole
suite; it was never a production-provider stand-in even before this pivot** — `VlmsDbContext`'s own
query filters (`ConsentSensitiveDetails`/`DbsCheck`) and `SensitiveDataAuditInterceptor`
(`IMaterializationInterceptor`) operate at EF Core's LINQ-to-Entities/materialization layer, above
whichever relational provider is plugged in underneath (`UseSqlServer`/`UseNpgsql`/SQLite) — none of
that logic is expressed in provider-specific SQL, so its behaviour is provider-independent by
construction, not just by observation. This was checked, not assumed: every existing
`SensitiveDataAccessControlTests`/`ConsentRecordServiceTests`/`DbsCheckServiceTests`/etc. test still
passes unchanged after the provider swap (166/166 including new tests, no test logic touched), which
is exactly the confirmation this decision needed rather than trusting the reasoning alone.

**What genuinely is provider-specific, and how it's still tested:** only raw SQL DDL written via
`MigrationBuilder.Sql()` — currently just `RevokeUpdateDeleteOnSensitiveDataAccessLog`'s tamper-
protection statement (`REVOKE UPDATE, DELETE ... FROM PUBLIC`, PostgreSQL-specific syntax with
PostgreSQL-specific semantics — see [access-control.md](access-control.md)). That migration is
never exercised against SQLite (which has no `REVOKE`/database-role model at all); `MigrationsTests.cs`
generates the real migration SQL in-process via `IMigrator.GenerateScript()` against an
Npgsql-configured context (no live connection needed) and asserts on the literal generated text —
the same "structurally correct, not yet exercised against a live resource" status this codebase
already gives `AzureBlobStorage` before its live dependency existed, now given to `UseNpgsql` DDL
instead of `UseSqlServer` DDL.

**Testcontainers-backed PostgreSQL integration tests were considered and explicitly not built**: the
actual risk they'd cover — does `REVOKE ... FROM PUBLIC` really behave as documented against a live
server — was addressed by reading current PostgreSQL documentation directly (cited in the migration's
doc comment and access-control.md) rather than by standing up new test infrastructure a solo-
maintained "fewest moving parts" build (adr/0003's reasoning) would then have to keep running. If a
genuine PostgreSQL-specific behavioural bug is ever suspected (not just "we haven't tried it against
a real server yet"), that's the trigger to revisit this, not a standing TODO.

## What "real, not tautological" means here (per two adversarial checker reviews)

- Deny tests must exercise an actual mismatch (an unlinked `ParentGuardian`, a `Student` with a different `AppUserId`), not just "the mock returns false".
- Multi-row assertions must check exact counts (`Assert.Equal(2, ...)`), not "at least one" or existence checks.
- Both the allow and the deny path for each rule should have a test — a handler with only a positive test has an unverified deny path, which is exactly the kind of gap this codebase's checker reviews have been built to catch.
