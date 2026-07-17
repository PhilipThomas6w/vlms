## Goal
Build the VLMS MVP per `docs/VISION.md`'s current-increment acceptance criteria: curriculum management, progress tracking, safeguarding & consent (with the corrected access-control model), parent engagement, and reporting.

## Done
- [x] Solution scaffolded: `Vlms.Domain`, `Vlms.Infrastructure`, `Vlms.Web` (Blazor Web App, Server interactivity), `Vlms.Tests` (xUnit), wired per `docs/design/low-level-design.md`'s solution structure. One real domain type (`Rank`) and one real passing test. `build/verify.ps1` wired to real `dotnet build -warnaserror` / `dotnet test` / secrets-scan stages, confirmed exiting 0. — commit pending (init-harness commit)

## In progress
- EF Core data model: all entities from `docs/design/data-design.md` (`Rank`, `Lesson`, `LessonChangeProposal`, `Student`, `ParentGuardian`, `StudentGuardianLink`, `StudentLessonCompletion`, `Certificate`, `RankBadge`, `StudentBadge`, `StudentRankProgress`, `ConsentRecord`, `ConsentSensitiveDetails`, `DbsCheck`, `AppUser`, `UserRole`, `SensitiveDataAccessLog`), `DbContext`, migrations, the EF Core global query filters and `IMaterializationInterceptor` audit logging from `docs/adr/0004-sensitive-data-access-control.md` — built in from the start, not retrofitted.

## Next
1. Microsoft Entra External ID integration (`docs/adr/0001-technology-stack.md`), `AppUser`/`UserRole` provisioning, and the role- plus resource-based authorization policies from `docs/adr/0002-roles-as-application-claims.md` and `docs/design/low-level-design.md`.
2. Curriculum management: `LessonProposalService` (propose/approve/reject/resubmit) and the Teacher/Approver UI.
3. Progress tracking: `CompletionService`, `PromotionService` (auto-promotion), `CertificateService` (QuestPDF PDF generation to Blob Storage).
4. Guardian-link creation flow (Admin/Teacher only, at student registration) — FR-004.
5. Safeguarding & consent: consent/DBS management UI, `ConsentExpiryJob` WebJob (expiry blocking, escalation, at-risk flagging at 8 weeks) — `docs/adr/0003-scheduled-jobs-webjobs.md`.
6. Parent dashboard + `NotificationService` (Azure Communication Services Email) with retry/escalation for safeguarding-critical notifications.
7. Reporting screens: core progress stats + at-risk flagging.
8. PWA manifest/service worker for installability (deferred design detail from ADR-0001).
9. Wire the `build/verify.ps1` full-only stage: WCAG 2.2 AA accessibility check and an OWASP ASVS 5.0 access-control review, ahead of the delivery gate.

## Blocked / needs decision
(none)

## Log
- 2026-07-17: init-harness — scaffolded solution structure, wired and confirmed a real green `build/verify.ps1` baseline, seeded queue from the approved design gate package.
