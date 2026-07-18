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
- [progress-tracking.md](progress-tracking.md) — `CompletionService`/`PromotionService`/`CertificateService`, the `IBlobStorage` abstraction, and the auto-promotion rank-ladder logic.
- [guardian-links.md](guardian-links.md) — `GuardianLinkService` (FR-004: Admin/Teacher-only `StudentGuardianLink` creation, never parent self-service), the new `AnyRoleRequirement`/`AnyRoleAuthorizationHandler` authorization mechanism, and the guardian-links page.
- [student-registration.md](student-registration.md) — `StudentRegistrationService`: creates the `Student` record, opens their first `StudentRankProgress` row at the starting rank, and creates a guardian link via `GuardianLinkService`. Closes `PromotionService`'s "no open StudentRankProgress row" precondition.
- [safeguarding-consent.md](safeguarding-consent.md) — `ConsentRecordService`/`DbsCheckService` (consent/DBS management UI), the `ConsentExpiryJob` WebJob (expiry/DBS flagging, 8-week at-risk/disengagement flagging, log-based escalation), `SystemCurrentUserContext`, and the new `Vlms.Jobs` WebJob host project.
- [testing.md](testing.md) — test conventions: the SQLite-in-memory pattern, `FakeCurrentUserContext`, what "real, not tautological" means in this repo's tests.

## Current build state (as of this refresh)

Seven `STATE.md` items are Done: the EF Core data model + ADR-0004 access control, Entra sign-in + provisioning + authorization, curriculum management (`LessonProposalService` + Teacher/Approver pages, plus the interactive-render-mode auth-resolution fix it depended on), progress tracking (`CompletionService`/`PromotionService`/`CertificateService`, service-layer only — no UI yet), guardian-link creation (`GuardianLinkService` + the guardian-links page, FR-004), student registration/enrolment (`StudentRegistrationService` + the registration page — creates the `Student` and its first `StudentRankProgress` row, and calls `GuardianLinkService` for the guardian link), and safeguarding & consent (`ConsentRecordService`/`DbsCheckService` + two Admin/SafeguardingOfficer-gated pages, plus the `ConsentExpiryJob` WebJob and its `Vlms.Jobs` host — see [safeguarding-consent.md](safeguarding-consent.md)). `Vlms.Web/Components/Pages/` has real VLMS UI (`Home.razor`, `Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`, `Guardianship/GuardianLinks.razor`, `Registration/RegisterStudent.razor`, `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`) alongside the template's `Error.razor`/`NotFound.razor`; progress tracking still has no consuming page, so `AzureBlobStorage`/`CompletionService`/`PromotionService`/`CertificateService` are not wired into `Program.cs`. See `STATE.md` for the live queue.
