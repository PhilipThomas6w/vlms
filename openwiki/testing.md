# Testing conventions

`tests/Vlms.Tests/`, xUnit. `dotnet test` is a `build/verify.ps1` gate stage — every test that exists must actually pass, not be skipped or stubbed to green.

## The SQLite-in-memory pattern (the reference: `Infrastructure/SensitiveDataAccessControlTests.cs`)

This repo's rule for testing anything touching `VlmsDbContext`: use a **real SQLite in-memory database**, not the EF Core `InMemory` provider — `InMemory` doesn't enforce relational constraints or faithfully support query-filter semantics, and this codebase's query filters are exactly the thing that needs faithful testing (see [access-control.md](access-control.md)).

Two non-obvious requirements, both load-bearing (discovered the hard way during the access-control work, per the maker's session):

1. **Resolve `VlmsDbContext` via a real DI container** (`AddDbContext` + `ServiceProvider`), not `new VlmsDbContext(...)` directly. EF Core only wires a context's internal service provider back to the application's `IServiceProvider` when the context is constructed through DI — and `materializationData.Context.GetService<T>()` inside `SensitiveDataAuditInterceptor` depends on that wiring. A bare `new VlmsDbContext(...)` in a test would silently fail to exercise the same resolution path production uses.
2. **Give every context the connection string, not a shared `SqliteConnection` instance.** EF's SQLite provider re-registers SQL functions on whichever physical connection it wraps, which SQLite refuses while that connection has an active statement — a real conflict when the audit interceptor opens a second context mid-read (see [access-control.md](access-control.md)). Use a named, shared-cache in-memory database (`Data Source=file:<name>;Mode=Memory;Cache=Shared`) kept alive by one long-lived anchor connection, so each context opens its own physical connection but sees the same data.

If a new test needs a `VlmsDbContext`, copy this pattern rather than reinventing it — don't drop back to `InMemory` or a shared connection instance for convenience. Further examples beyond the reference file: `EntraCurrentUserContextTests`, `UserProvisioningServiceTests`, `AuthenticationStatePrincipalResolverTests`, `LessonProposalServiceTests` (all `tests/Vlms.Tests/Infrastructure/`).

## `FakeCurrentUserContext` (`Infrastructure/FakeCurrentUserContext.cs`)

A minimal `ICurrentUserContext` test double: `new FakeCurrentUserContext(userId, params Role[] roles)`. Used to simulate "the caller" in tests — it only supplies role/identity data; the actual authorization/query-filter logic under test is always the real production code, never mocked. (Its doc comment, like `ICurrentUserContext.cs`'s, still says the real implementation is "a later STATE.md item" — stale now that `EntraCurrentUserContext` exists, harmless but worth a pass next time either file is touched.)

## `ListLogger<T>` (`Infrastructure/ListLogger.cs`)

A minimal `ILogger<T>` test double, same spirit as `FakeCurrentUserContext`/`InMemoryBlobStorage`: records every logged `(LogLevel, formatted message)` pair to a list so a test can assert on log severity (e.g. `ConsentExpiryJob`'s Error-vs-Warning escalation distinction, see [safeguarding-consent.md](safeguarding-consent.md)) without pulling in a mocking library.

## What "real, not tautological" means here (per two adversarial checker reviews)

- Deny tests must exercise an actual mismatch (an unlinked `ParentGuardian`, a `Student` with a different `AppUserId`), not just "the mock returns false".
- Multi-row assertions must check exact counts (`Assert.Equal(2, ...)`), not "at least one" or existence checks.
- Both the allow and the deny path for each rule should have a test — a handler with only a positive test has an unverified deny path, which is exactly the kind of gap this codebase's checker reviews have been built to catch.
