# Data Design

Status: in progress

Relational model in Azure SQL Database, accessed via EF Core (see `adr/0001-technology-stack.md`). Entities below supersede the prior build's Dataverse model — retained conceptually where still valid, revised where discovery resolved an ambiguity (certificates and badges are now first-class tracked entities, not implicit).

## Entities

| Entity | Key fields | Notes |
|---|---|---|
| `Rank` | Id, Order, Code, Name | The 10-level progression ladder. |
| `Lesson` | Id, RankId, Code, Title, ContentBlobKey, IsActive | Current live version of a lesson's content. |
| `LessonChangeProposal` | Id, LessonId (nullable — null for a brand-new lesson), ProposedByUserId, ChangeType (Create/Edit/Retire), ProposedContent, Status (Pending/Approved/Rejected), ApproverUserId, ApprovalComments, SubmittedAt, DecidedAt, ResubmissionOfProposalId (self-referencing) | Any Teacher proposes; only Approver decides. Rejection supports comments + resubmission via `ResubmissionOfProposalId` chaining back to the original. Approving applies `ProposedContent` onto `Lesson`. |
| `Student` | Id, Name, DOB, CurrentRankId, Status (Active/Inactive/Graduated), EnrolmentDate, AssignedTeacherUserId | Duplicate-matching on Name+DOB at registration (carried over as a hypothesis — confirm at build time). |
| `ParentGuardian` | Id, Name, ContactInfo, IsPrimary | |
| `StudentGuardianLink` | StudentId, ParentGuardianId, CreatedByUserId | Many-to-many: a parent may have several children; a student may have more than one guardian. **Created only by Admin/Teacher** (at student registration), never by parent self-service — a parent cannot self-declare a relationship to a child. See "Guardian link verification" below. |
| `StudentLessonCompletion` | Id, StudentId, LessonId, CompletedByUserId, CompletedAt, Note (optional), IsReversed, ReversedAt | Teacher marks and can self-correct (`IsReversed`/`ReversedAt`) — no separate Admin correction gate. |
| `Certificate` | Id, StudentLessonCompletionId, GeneratedAt, BlobKey (PDF) | Auto-generated on completion (QuestPDF). Real tracked record, not implicit. |
| `RankBadge` | Id, RankId, ImageBlobKey | One badge per rank. |
| `StudentBadge` | Id, StudentId, RankBadgeId, AwardedAt | Awarded on promotion. Real tracked record, not implicit. |
| `StudentRankProgress` | Id, StudentId, RankId, StartedAt, CompletedAt | Promotion history; a new row starts when a student enters a rank, closes when they complete it (auto-promotion trigger). |
| `ConsentRecord` | Id, StudentId, PeriodStart, PeriodEnd (annual), PhotoMediaConsent, TransportOffsiteConsent, DataSharingConsent, Status (Pending/Approved/Rejected), SubmittedByParentId, ApprovedByUserId (Safeguarding Officer or Admin — **not** the Approver role, which is curriculum-only), ExpiryDate | **Contains no sensitive fields** (see design-review correction below) — `Status`/`ExpiryDate` must be readable by Teacher to enforce the consent-blocks-completion rule (FR-003), so this entity is deliberately *not* subject to the sensitive-data query filter. Annual renewal. Expiry blocks `StudentLessonCompletion` creation for that student. |
| `ConsentSensitiveDetails` | Id, ConsentRecordId (1:1), EmergencyMedicalInfo, DietarySEN, EmergencyContact | **Split out from `ConsentRecord`** at design review: EF Core global query filters restrict access at the whole-entity/row level, not per-column, so a field that must be hidden from a role that otherwise needs the rest of the row cannot be masked in place — it has to live in its own entity. This entity is subject to the same whole-entity restriction as `DbsCheck`: visible only to Admin and Safeguarding Officer. |
| `DbsCheck` | Id, TeacherUserId, CheckDate, ExpiryDate, CertificateNumber, Status | **Access restricted entirely to Safeguarding Officer and Admin** via the sensitive-data query filter — Teacher and Approver have no access at all (whole entity, not column-level, so the filter mechanism applies correctly here). |
| `AppUser` | Id, EntraObjectId, DisplayName, Email | Maps to the Entra External ID identity. |
| `UserRole` | UserId, Role | Role ∈ {Admin, Teacher, Approver, Parent, Student, SafeguardingOfficer}. A single user may hold more than one role (e.g. a Teacher who is also the Approver). |
| `SensitiveDataAccessLog` | Id, UserId, Entity (`DbsCheck`/`ConsentSensitiveDetails`), EntityId, AccessedAt, AccessType (View/Export) | Audit trail of **reads**, not just writes, of safeguarding data — see security note below. Write-once; update/delete denied at the database permission level (see below), not just by application convention. |

## Relationships (summary)

- `Rank` 1:N `Lesson`, 1:N `RankBadge`.
- `Lesson` 1:N `LessonChangeProposal`, 1:N `StudentLessonCompletion`.
- `Student` 1:N `StudentLessonCompletion`, 1:N `StudentRankProgress`, 1:N `StudentBadge`, 1:N `ConsentRecord`; N:N `ParentGuardian` via `StudentGuardianLink`.
- `ConsentRecord` 1:1 `ConsentSensitiveDetails` (the entity split described above).
- `StudentLessonCompletion` 1:1 `Certificate` (nullable until generated).
- `AppUser` 1:N `UserRole`; referenced by `Student.AssignedTeacherUserId`, `LessonChangeProposal.ProposedByUserId`/`ApproverUserId`, `StudentLessonCompletion.CompletedByUserId`, `ConsentRecord.ApprovedByUserId`, `DbsCheck.TeacherUserId`.

## Sensitive-entity access restriction (corrected at second design review)

Per `governance/security-compliance.md` (UK GDPR/DPA 2018, special-category + children's data) and `adr/0004-sensitive-data-access-control.md`:

- **Design correction:** the first pass at this design specified column-level masking of three `ConsentRecord` fields via an EF Core global query filter. That doesn't work: a global query filter is a row-inclusion predicate — it can only include or exclude an entire row, not redact individual columns within a row a role otherwise needs (Teacher must read `ConsentRecord.Status`/`ExpiryDate`). Fixed by splitting the sensitive fields into their own entity, `ConsentSensitiveDetails` (see Entities table), so the *same* whole-entity filter pattern that correctly restricts `DbsCheck` now also correctly restricts the sensitive consent fields.
- `DbsCheck` (whole entity): visible only to Admin and Safeguarding Officer. Teacher and Approver have no access.
- `ConsentSensitiveDetails` (whole entity, containing what were the `EmergencyMedicalInfo`/`DietarySEN`/`EmergencyContact` fields): visible only to Admin and Safeguarding Officer. Teacher and Approver can still read the (now separate) `ConsentRecord` row for `Status`/`ExpiryDate`.
- The Approver role's remit is curriculum/lesson-change approval only (`LessonChangeProposal.ApproverUserId`) — it has no involvement in consent or DBS approval, and no elevated PII access beyond a regular Teacher.
- **Enforcement mechanism:** EF Core global query filters on `DbsCheck` and `ConsentSensitiveDetails`, configured on the `DbContext`, keyed by the current caller's role. A developer cannot accidentally leak either entity by writing a new query or report without explicitly bypassing the filter (`IgnoreQueryFilters()`), which is a deliberate, greppable, reviewable act. See [EF Core global query filters](https://learn.microsoft.com/ef/core/querying/filters).

## Audit logging of sensitive-data reads

- Every read of a `DbsCheck` or `ConsentSensitiveDetails` row writes a `SensitiveDataAccessLog` row (who, what, when). This is a genuine control gap identified at design review — the prior model only logged who *created/approved* a record, not who subsequently *viewed* it. Given the entire justification for managing this data in-system is safeguarding, an access log is not optional.
- **Write mechanism (named at second design review):** a `DbCommandInterceptor` registered globally on the `DbContext` (EF Core's [interceptor](https://learn.microsoft.com/ef/core/logging-events-diagnostics/interceptors) mechanism), which inspects executed commands for the two sensitive tables and writes the audit row after successful execution, using the ambient current-user context. This is structural — like the query filters, it applies to every query against these tables regardless of call site — rather than a per-repository convention a future developer could forget.
- **Tamper protection (added at second design review):** `SensitiveDataAccessLog` has `UPDATE`/`DELETE` denied at the database permission level (SQL Server `DENY` on the application's database principal) for this table specifically, not just an "append-only" convention in application code.
- **Retention (added at second design review):** audit log entries are retained **6 years** regardless of the referenced record's own retention period (matching the longer DBS retention bar, since the log's purpose is accountability, not the underlying record's lifecycle) — a deliberate choice for the owner to override if a different period is preferred.

## Guardian link verification

- A `StudentGuardianLink` is created **only by Admin (or a Teacher acting under Admin's authority) at student registration** — never by parent self-service. The Admin/Teacher enters the guardian's details and issues an Entra External ID invitation already scoped to the correct `Student` record.
- **Evidentiary basis (added at second design review):** the Admin/Teacher creating the link should do so through the same channel already used to enrol the student (i.e. the guardian is who the family named at enrolment, or someone already known to the programme) — not a separate, formal identity-verification process. This is deliberately lightweight, proportionate to a small, personally-known membership, not a large anonymous user base.
- This closes a design-review gap: Entra External ID's self-service sign-up would otherwise let a parent register and claim a relationship to a child with no verification that the claim is genuine, before ever touching medical/consent data.

## Data ownership & lifecycle

- System of record: this database is authoritative for all entities above (no external source of truth, per `design/integration.md`).
- Retention/deletion policy (UK GDPR requirement): student records (including consent, medical/dietary/SEN data) retained **3 years from the student leaving/graduating** the programme. DBS (Disclosure and Barring Service, gov.uk) checks are held against Teachers, not students, and are retained **6 years** after expiry/the teacher leaving.
- **Deletion enforcement — interim decision:** manual, Admin-triggered deletion once a record passes its retention period (an Admin-facing "records due for deletion" report from `ConsentExpiryJob`'s wider sweep). Full automated purge is deferred — acceptable for solo-scale operation, but recorded here as a deliberate interim choice, not an oversight (per design review).
- **Hard delete vs anonymisation — decided (second design review):** hard delete. Simpler to reason about and implement solo, avoids the adequacy debate over whether an anonymisation technique genuinely prevents re-identification (a live UK GDPR concern), and nothing in the confirmed requirements needs retained-but-anonymised data for ongoing reporting/analytics. Owner can override if historical trend reporting turns out to matter later.
- No migration — clean start (confirmed, `raid.md` A-003).
