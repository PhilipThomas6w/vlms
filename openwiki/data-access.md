# Data access: VlmsDbContext

`src/Vlms.Infrastructure/VlmsDbContext.cs`. One `DbSet<T>` per entity in [domain.md](domain.md); `OnModelCreating` configures every relationship via Fluent API. Design source: `docs/design/data-design.md`.

## Conventions to preserve

- **All keys are application-assigned** (`ValueGeneratedNever`), except `SensitiveDataAccessLog.Id` (`ValueGeneratedOnAdd`). This was a deliberate choice for determinism in the access-control tests (see the `OnModelCreating` comment) — don't "fix" it to database identity without checking what depends on the current behaviour (`UserProvisioningService`'s `MaxAsync + 1` pattern, for one).
- **Two entities carry `HasQueryFilter`**: `ConsentSensitiveDetails` and `DbsCheck`. See [access-control.md](access-control.md) before adding, removing, or modifying any query filter in this file — this is the one part of the schema with the most design-review scrutiny behind it.
- **`SensitiveDataAuditInterceptor.Instance`** is registered in `OnConfiguring` via `AddInterceptors`. It's a singleton (see [access-control.md](access-control.md) for why that's safe).
- **`ConsentRecord` ↔ `ConsentSensitiveDetails`** is 1:1, FK on the dependent (`ConsentSensitiveDetails.ConsentRecordId`), cascade delete. **`LessonChangeProposal.ResubmissionOfProposalId`** is a self-referencing FK (restrict delete) supporting the propose → reject-with-comments → resubmit workflow.

## Migrations (`src/Vlms.Infrastructure/Migrations/`)

- `20260717224824_InitialCreate` — the full 16-entity model, ADR-0004 mechanism included from the start (not retrofitted).
- `20260717232412_AddAppUserLinkToStudentAndParentGuardian` — adds `Student.AppUserId` / `ParentGuardian.AppUserId` (nullable, restrict-delete FKs to `AppUser`), for the self/parent-login gap found and fixed during the authentication work. Does not touch the query filters or interceptor.

`VlmsDbContextFactory` (`src/Vlms.Infrastructure/VlmsDbContextFactory.cs`) is the EF Core design-time factory used by `dotnet ef` tooling — it has no request context, so it uses `NullCurrentUserContext`. The gate's own `dotnet ef migrations has-pending-model-changes` check (run by the maker/checker cycle, not currently a `build/verify.ps1` stage) is the way to catch model/migration drift; consider wiring it into `verify.ps1` if migrations become a frequent source of drift.

## Provider split: SQL Server (production) vs SQLite (tests)

`VlmsDbContext.OnConfiguring` doesn't choose a provider — that's done by whoever constructs the `DbContextOptions`. `Vlms.Web/Program.cs` uses `UseSqlServer`; tests use SQLite in-memory. See [testing.md](testing.md) for why, and for a caveat about SQLite's connection-sharing behaviour under this context's own "open a second context" patterns.
