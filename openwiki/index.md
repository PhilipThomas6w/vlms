# VLMS codebase wiki (openwiki)

Agent-native reference for the current state of the code, refreshed as the codebase changes (`loop-harness:doc-refresh`). This describes what is actually built, not the design intent — for design intent and rationale, read `docs/` (requirements, design, ADRs) and `docs/VISION.md` first.

Read order for a new session: `docs/VISION.md` → `STATE.md` → this index → the specific page(s) below relevant to the task.

## Pages

- [architecture.md](architecture.md) — solution shape, project dependency graph, where each concern lives.
- [domain.md](domain.md) — `Vlms.Domain`: entities, the `Role` enum, `ICurrentUserContext`.
- [data-access.md](data-access.md) — `Vlms.Infrastructure`: `VlmsDbContext`, EF Core configuration, migrations.
- [access-control.md](access-control.md) — the ADR-0004 sensitive-data mechanism: query filters + read-audit interceptor. Read this before touching anything near `DbsCheck`/`ConsentSensitiveDetails`/`SensitiveDataAccessLog`.
- [authentication-authorization.md](authentication-authorization.md) — Entra External ID sign-in, `AppUser`/`UserRole` provisioning, role- and resource-based authorization, and how the caller's `ClaimsPrincipal` is resolved for interactive Blazor Server components.
- [web.md](web.md) — `Vlms.Web`: `Program.cs` wiring, Blazor Server interactive render mode, `AuthorizeRouteView`-based page gating.
- [curriculum.md](curriculum.md) — `LessonProposalService` (propose/approve/reject/resubmit) and the Teacher/Approver pages.
- [testing.md](testing.md) — test conventions: the SQLite-in-memory pattern, `FakeCurrentUserContext`, what "real, not tautological" means in this repo's tests.

## Current build state (as of this refresh)

Three `STATE.md` items are Done: the EF Core data model + ADR-0004 access control, Entra sign-in + provisioning + authorization, and curriculum management (`LessonProposalService` + Teacher/Approver pages, plus the interactive-render-mode auth-resolution fix it depended on). `Vlms.Web/Components/Pages/` has real VLMS UI now (`Home.razor`, `Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`) alongside the template's `Error.razor`/`NotFound.razor`. See `STATE.md` for the live queue.
