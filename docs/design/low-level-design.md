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
  - A Parent may only view/act on `Student` records linked to them via `StudentGuardianLink`.
  - A Student may only view their own record.
  - A Teacher sees **all** students in the programme (confirmed) — not scoped to `Student.AssignedTeacherUserId`, so teachers can cover for each other.
- Column-level masking and whole-record access restriction (`data-design.md`): enforced at the data-access layer (a query-shaping method per sensitive field set / entity, keyed by caller's role) rather than database-level column encryption, to keep the solo-maintainer surface area small. `DbsCheck` is restricted entirely to Admin/Safeguarding Officer; `ConsentRecord`'s sensitive fields are masked from Teacher and Approver. [TBC: confirm this satisfies the OWASP ASVS 5.0 access-control chapter at build time.]

## Key domain services (`Vlms.Domain`)

- **`LessonProposalService`** — create/submit a proposal; `Approve(proposalId, approverUserId)` applies `ProposedContent` to `Lesson`; `Reject(proposalId, approverUserId, comments)` sets Status and enables resubmission via `ResubmissionOfProposalId`.
- **`CompletionService`** — `MarkComplete(studentId, lessonId, teacherUserId, note?)`: blocked if the student's active `ConsentRecord` is expired/missing (hard business rule, `functional.md`); on success, triggers `PromotionService` check and `CertificateService.Generate(...)`.
- **`PromotionService`** — after a completion, checks whether all active `Lesson`s in the student's `CurrentRankId` are complete; if so, closes the current `StudentRankProgress` row, opens the next, advances `Student.CurrentRankId`, and awards the `RankBadge` via a new `StudentBadge`. At the final rank, sets `Student.Status = Graduated` instead of advancing further.
- **`CertificateService`** — generates a PDF via QuestPDF from a template, uploads to Blob Storage, writes a `Certificate` row.
- **`ConsentExpiryJob`** (WebJob, scheduled) — daily sweep: flags consents/DBS checks expiring within a configurable window and expired ones, triggers `NotificationService` emails, escalates to Admin/Safeguarding Officer. Also runs the at-risk/disengaged student flagging: no lesson completion within 8 weeks (`functional.md`).
- **`NotificationService`** — wraps Azure Communication Services Email for: lesson completion/promotion confirmations to parents, consent-expiry reminders, at-risk flags to Admin.

## Open items to resolve before build

None outstanding — retention periods (3 years student, 6 years DBS) and role/PII visibility boundaries are confirmed above and in `data-design.md`.
