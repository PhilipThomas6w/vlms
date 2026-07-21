# Architecture (as built)

See `docs/design/architecture.md` and `docs/adr/` for design intent and rationale. This page is the as-built shape.

## Solution layout

```
Vlms.slnx
src/
  Vlms.Domain/            entities, Role enum, ICurrentUserContext, IBlobStorage, INotificationService (no EF Core dependency)
  Vlms.Infrastructure/     EF Core DbContext, auth, provisioning, migrations, services (depends on Vlms.Domain)
  Vlms.Web/                Blazor Web App, Server interactivity (depends on Vlms.Domain + Vlms.Infrastructure)
  Vlms.Jobs/               Scheduled-job host: ConsentExpiryJob + ConsentExpiryNotifier, run to
                           completion once daily (adr/0003, amended by adr/0005 to run via a systemd
                           timer/service on the VPS instead of an Azure WebJob) — depends on
                           Vlms.Domain + Vlms.Infrastructure, same as Vlms.Web; not deployed by
                           anything in this repo yet (no live VPS environment) — see
                           openwiki/safeguarding-consent.md
deploy/
  systemd/                 vlms-jobs.service + vlms-jobs.timer (systemd unit pair invoking Vlms.Jobs's
                           published output daily at 03:00 UTC) + README.md — see
                           openwiki/safeguarding-consent.md
tests/
  Vlms.Tests/               xUnit, SQLite-in-memory (depends on Vlms.Domain; references Infrastructure types via project ref)
build/
  verify.ps1                the gate: dotnet build -warnaserror, dotnet test, secrets scan, and
                             (full runs only) an ASVS 5.0 V8 access-control review + a WCAG 2.2 AA
                             accessibility check — see openwiki/verify-gate.md
  check-secrets.ps1         the secrets-scan stage's script (runs in both -Fast and full; gitleaks if
                             present, else a hardened regex fallback — see openwiki/verify-gate.md)
  check-access-control.ps1  the ASVS stage's script (static scan + checklist-currency gate)
  check-accessibility.ps1   the WCAG stage's script (static scan + checklist-currency gate)
  lib-checklist-currency.ps1  shared content-hash helper both stages above dot-source
docs/                       design source of truth — read before changing anything cross-cutting
gates/design/                rendered design gate package (.docx) — reference only, never edited
```

No separate API project exists or is planned — `docs/design/integration.md` confirms VLMS is standalone with no external system needing programmatic access.

## Dependency direction

`Vlms.Web` → `Vlms.Infrastructure` → `Vlms.Domain`. `Vlms.Domain` has no dependency on EF Core or ASP.NET Core — it defines entities and the `ICurrentUserContext` abstraction only. This is why `ICurrentUserContext` lives in Domain but all three of its implementations (`NullCurrentUserContext`, `SystemCurrentUserContext`, `IdentityCurrentUserContext`) live in Infrastructure — and why `AppUser` (Domain) stays a plain POCO while ASP.NET Core Identity's own credential row, `VlmsIdentityUser`, is a separate type in Infrastructure (`Vlms.Infrastructure/Identity/`) — see [authentication-authorization.md](authentication-authorization.md)'s coexistence-design section.

## Key cross-cutting mechanisms (each has its own page)

- **Sensitive-data access control** (`docs/adr/0004-sensitive-data-access-control.md`) — EF Core global query filters + an `IMaterializationInterceptor` audit log. See [access-control.md](access-control.md).
- **Authentication/authorization** (`docs/adr/0002`) — self-rolled ASP.NET Core Identity (TOTP MFA) + app-managed roles + resource-based handlers (originally Microsoft Entra External ID, `docs/adr/0001`; replaced by STATE.md's "Identity" item as part of the hosting pivot, adr/0005). See [authentication-authorization.md](authentication-authorization.md).
- **Scheduled work** (`docs/adr/0003-scheduled-jobs-webjobs.md`, amended by `docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`) — a systemd timer/service pair on the self-hosted VPS (`deploy/systemd/`), replacing the original Azure App Service WebJobs folder convention. Implemented: `ConsentExpiryJob` (daily consent/DBS expiry + 8-week at-risk flagging) plus `ConsentExpiryNotifier` (safeguarding-critical email escalation), hosted by the `Vlms.Jobs` console project above — not yet deployed anywhere (no live VPS environment). See [safeguarding-consent.md](safeguarding-consent.md) and [notifications.md](notifications.md).

## Target framework and stack

.NET 10 (SDK 10.0.302 at last refresh), Blazor Web App with **Server** interactivity (not Auto/WebAssembly — a scaffolding-time decision favouring fewest moving parts for a solo-maintained app, see `STATE.md`'s init-harness log entry). EF Core against self-hosted PostgreSQL (`UseNpgsql`, `docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` — supersedes the original Azure SQL Database choice in `docs/adr/0001-technology-stack.md`); tests use SQLite in-memory instead (see [testing.md](testing.md)) for speed and portability, not because the production provider differs — the query-filter/audit-interceptor mechanism this codebase cares most about testing is provider-independent (see testing.md's "Test-provider strategy" section).
