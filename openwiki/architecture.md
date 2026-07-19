# Architecture (as built)

See `docs/design/architecture.md` and `docs/adr/` for design intent and rationale. This page is the as-built shape.

## Solution layout

```
Vlms.slnx
src/
  Vlms.Domain/            entities, Role enum, ICurrentUserContext (no EF Core dependency)
  Vlms.Infrastructure/     EF Core DbContext, auth, provisioning, migrations (depends on Vlms.Domain)
  Vlms.Web/                Blazor Web App, Server interactivity (depends on Vlms.Domain + Vlms.Infrastructure)
tests/
  Vlms.Tests/               xUnit, SQLite-in-memory (depends on Vlms.Domain; references Infrastructure types via project ref)
build/
  verify.ps1                the gate: dotnet build -warnaserror, dotnet test, secrets scan, and
                             (full runs only) an ASVS 5.0 V8 access-control review + a WCAG 2.2 AA
                             accessibility check — see openwiki/verify-gate.md
  check-access-control.ps1  the ASVS stage's script (static scan + checklist-currency gate)
  check-accessibility.ps1   the WCAG stage's script (static scan + checklist-currency gate)
  lib-checklist-currency.ps1  shared content-hash helper both stages above dot-source
docs/                       design source of truth — read before changing anything cross-cutting
gates/design/                rendered design gate package (.docx) — reference only, never edited
```

No separate API project exists or is planned — `docs/design/integration.md` confirms VLMS is standalone with no external system needing programmatic access.

## Dependency direction

`Vlms.Web` → `Vlms.Infrastructure` → `Vlms.Domain`. `Vlms.Domain` has no dependency on EF Core or ASP.NET Core — it defines entities and the `ICurrentUserContext` abstraction only. This is why `ICurrentUserContext` lives in Domain but both its implementations (`NullCurrentUserContext`, `EntraCurrentUserContext`) live in Infrastructure.

## Key cross-cutting mechanisms (each has its own page)

- **Sensitive-data access control** (`docs/adr/0004-sensitive-data-access-control.md`) — EF Core global query filters + an `IMaterializationInterceptor` audit log. See [access-control.md](access-control.md).
- **Authentication/authorization** (`docs/adr/0001`, `docs/adr/0002`) — Entra External ID + app-managed roles + resource-based handlers. See [authentication-authorization.md](authentication-authorization.md).
- **Scheduled work** (`docs/adr/0003-scheduled-jobs-webjobs.md`) — Azure App Service WebJobs; not yet implemented (`STATE.md` Next item 4, `ConsentExpiryJob`).

## Target framework and stack

.NET 10 (SDK 10.0.302 at last refresh), Blazor Web App with **Server** interactivity (not Auto/WebAssembly — a scaffolding-time decision favouring fewest moving parts for a solo-maintained app, see `STATE.md`'s init-harness log entry). EF Core against SQL Server (`UseSqlServer`) matching the Azure SQL Database choice in `docs/adr/0001-technology-stack.md`; tests use SQLite in-memory instead (see [testing.md](testing.md)) for speed and portability, not because the production provider differs.
