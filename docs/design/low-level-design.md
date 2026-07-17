# Low-Level Design

Status: in progress

## Solution structure

- `Vlms.Web` — the Blazor Web App (UI, Razor components, authorization policies, WebJob host).
- `Vlms.Domain` — entities (`data-design.md`), domain logic (promotion rules, proposal state machine).
- `Vlms.Infrastructure` — EF Core `DbContext`, Azure Blob Storage client, Azure Communication Services Email client, QuestPDF certificate generation.
- `Vlms.Tests` — unit/integration tests (see `quality/test-plan.md`).

No separate public API project — per `design/integration.md`, VLMS is standalone with no external system needing programmatic access. If that changes, a `Vlms.Api` project can be split out later without redesigning the domain/infrastructure layers.

## Authorization model

- Authentication: Microsoft Entra External ID (OIDC), per `adr/0001-technology-stack.md`.
- Authorization: role-based (`UserRole.Role` — Admin/Teacher/Approver/Parent/Student/SafeguardingOfficer) implemented as ASP.NET Core authorization policies, **plus** resource-based checks where a flat role isn't enough:
  - A Parent may only view/act on `Student` records linked to them via `StudentGuardianLink` — and that link is only ever created by Admin/Teacher (see `data-design.md` — Guardian link verification), never claimed by the parent themselves.
  - A Student may only view their own record.
  - A Teacher sees **all** students in the programme (confirmed) — not scoped to `Student.AssignedTeacherUserId`, so teachers can cover for each other.
- Whole-entity access restriction (`data-design.md`): `DbsCheck` and `ConsentSensitiveDetails` (the medical/dietary-SEN/emergency-contact fields, split out from `ConsentRecord` — see `adr/0004-sensitive-data-access-control.md` for why a query filter cannot mask individual columns) are restricted structurally via **EF Core global query filters** on the `DbContext`, not an opt-in per-call convention. `ConsentRecord` itself (status, expiry, non-sensitive flags) remains readable by Teacher/Approver, since `CompletionService` needs it to enforce the consent-blocks-completion rule. Every read of `DbsCheck`/`ConsentSensitiveDetails` is additionally written to `SensitiveDataAccessLog` via an `IMaterializationInterceptor` (`adr/0004`) — chosen over a raw-SQL `DbCommandInterceptor` because it gives direct typed access to the materialized entity's ID, correctly once per row.
- Test coverage for this model (whole-entity restriction, audit logging, guardian-link scoping) is the priority of the access-control test suite — see `quality/test-plan.md` TC-006/007/008 and TC-011/012.

## Key domain services (`Vlms.Domain`)

- **`LessonProposalService`** — create/submit a proposal; `Approve(proposalId, approverUserId)` applies `ProposedContent` to `Lesson`; `Reject(proposalId, approverUserId, comments)` sets Status and enables resubmission via `ResubmissionOfProposalId`.
- **`CompletionService`** — `MarkComplete(studentId, lessonId, teacherUserId, note?)`: blocked if the student's active `ConsentRecord.Status`/`ExpiryDate` shows expired/missing consent (hard business rule, `functional.md`) — reads only the non-sensitive `ConsentRecord`, never `ConsentSensitiveDetails`; on success, triggers `PromotionService` check and `CertificateService.Generate(...)`.
- **`PromotionService`** — after a completion, checks whether all active `Lesson`s in the student's `CurrentRankId` are complete; if so, closes the current `StudentRankProgress` row, opens the next, advances `Student.CurrentRankId`, and awards the `RankBadge` via a new `StudentBadge`. At the final rank, sets `Student.Status = Graduated` instead of advancing further.
- **`CertificateService`** — generates a PDF via QuestPDF from a template, uploads to Blob Storage, writes a `Certificate` row.
- **`ConsentExpiryJob`** (WebJob, scheduled — `adr/0003-scheduled-jobs-webjobs.md`) — daily sweep: flags consents/DBS checks expiring within a configurable window and expired ones, triggers `NotificationService` emails, escalates to Admin/Safeguarding Officer. Also runs the at-risk/disengaged student flagging: no lesson completion within 8 weeks (`functional.md`), and surfaces the "records due for deletion" report (`data-design.md` — deletion enforcement).
- **`NotificationService`** — wraps Azure Communication Services Email for: lesson completion/promotion confirmations to parents, consent-expiry reminders, at-risk flags to Admin. **Failure handling (design-review addition):** a failed send for a safeguarding-critical notification (expired consent, expired DBS) is retried with backoff and, if still failing, logged as an escalation-visible failure to Admin — a silent failure here must not be possible, since it's the mechanism that surfaces a safeguarding lapse. Non-critical notifications (e.g. routine completion confirmations) log failure without escalation.

## Open items to resolve before build

- Certificate delivery mechanism (download from dashboard vs emailed automatically) — not yet decided (`requirements/functional.md`).
- Confirm the entity-split + EF Core query filter + audit-interceptor design (`adr/0004-sensitive-data-access-control.md`) against the OWASP ASVS 5.0 access-control and logging chapters during implementation, via `quality/test-plan.md` TC-007/008/011.

Resolved at design review (two passes — the first pass's masking mechanism was itself corrected at the second pass, see `adr/0004`): retention periods (3 years student, 6 years DBS, 6 years audit log), role/PII visibility boundaries, guardian-link verification + evidentiary basis, read-audit logging with a named write mechanism and tamper protection, whole-entity restriction via the `ConsentRecord`/`ConsentSensitiveDetails` split, and hard-delete-vs-anonymisation — all confirmed above and in `data-design.md`.
