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
| `ConsentRecord` | Id, StudentId, PeriodStart, PeriodEnd (annual), PhotoMediaConsent, EmergencyMedicalInfo\*, DietarySEN\*, TransportOffsiteConsent, DataSharingConsent, EmergencyContact\*, Status (Pending/Approved/Rejected), SubmittedByParentId, ApprovedByUserId (Safeguarding Officer or Admin — **not** the Approver role, which is curriculum-only), ExpiryDate | \* = sensitive, column-level masked (see below). Annual renewal. Expiry blocks `StudentLessonCompletion` creation for that student. |
| `DbsCheck` | Id, TeacherUserId, CheckDate, ExpiryDate, CertificateNumber\*, Status | \* = sensitive. **Access restricted entirely to Safeguarding Officer and Admin** — Teacher and Approver have no access at all (not just masked fields; the whole record is out of scope for those roles). |
| `AppUser` | Id, EntraObjectId, DisplayName, Email | Maps to the Entra External ID identity. |
| `UserRole` | UserId, Role | Role ∈ {Admin, Teacher, Approver, Parent, Student, SafeguardingOfficer}. A single user may hold more than one role (e.g. a Teacher who is also the Approver). |
| `SensitiveDataAccessLog` | Id, UserId, Entity (`DbsCheck`/`ConsentRecord`), EntityId, AccessedAt, AccessType (View/Export) | Audit trail of **reads**, not just writes, of safeguarding data — see security note below. Append-only. |

## Relationships (summary)

- `Rank` 1:N `Lesson`, 1:N `RankBadge`.
- `Lesson` 1:N `LessonChangeProposal`, 1:N `StudentLessonCompletion`.
- `Student` 1:N `StudentLessonCompletion`, 1:N `StudentRankProgress`, 1:N `StudentBadge`, 1:N `ConsentRecord`; N:N `ParentGuardian` via `StudentGuardianLink`.
- `StudentLessonCompletion` 1:1 `Certificate` (nullable until generated).
- `AppUser` 1:N `UserRole`; referenced by `Student.AssignedTeacherUserId`, `LessonChangeProposal.ProposedByUserId`/`ApproverUserId`, `StudentLessonCompletion.CompletedByUserId`, `ConsentRecord.ApprovedByUserId`, `DbsCheck.TeacherUserId`.

## Column-level security / masking

Per `governance/security-compliance.md` (UK GDPR/DPA 2018, special-category + children's data):

- `DbsCheck` (the whole record, not just sensitive fields): visible only to Admin and Safeguarding Officer. Teacher and Approver have no access.
- `ConsentRecord.EmergencyMedicalInfo`, `ConsentRecord.DietarySEN`, `ConsentRecord.EmergencyContact`: masked from Teacher and Approver. Full visibility: Admin, Safeguarding Officer.
- The Approver role's remit is curriculum/lesson-change approval only (`LessonChangeProposal.ApproverUserId`) — it has no involvement in consent or DBS approval, and no elevated PII access beyond a regular Teacher.
- **Enforcement mechanism — decided:** EF Core global query filters (structural, applied at the `DbContext` level so every query is masked/restricted automatically), not an opt-in per-call convention. A developer cannot accidentally leak `DbsCheck`/`ConsentRecord` sensitive fields by writing a new query or report without explicitly bypassing the filter (`IgnoreQueryFilters()`), which is a deliberate, greppable, reviewable act. See [EF Core global query filters](https://learn.microsoft.com/ef/core/querying/filters).

## Audit logging of sensitive-data reads

- Every read of a `DbsCheck` record, or of a `ConsentRecord`'s masked/sensitive fields, writes a `SensitiveDataAccessLog` row (who, what, when). This is a genuine control gap identified at design review — the prior model only logged who *created/approved* a record, not who subsequently *viewed* it. Given the entire justification for managing this data in-system is safeguarding, an access log is not optional.
- Enforcement: applied in the same data-access layer as the EF Core global query filters above, so logging and masking cannot drift apart.

## Guardian link verification

- A `StudentGuardianLink` is created **only by Admin (or a Teacher acting under Admin's authority) at student registration** — never by parent self-service. The Admin/Teacher enters the guardian's details and issues an Entra External ID invitation already scoped to the correct `Student` record.
- This closes a design-review gap: Entra External ID's self-service sign-up would otherwise let a parent register and claim a relationship to a child with no verification that the claim is genuine, before ever touching medical/consent data.

## Data ownership & lifecycle

- System of record: this database is authoritative for all entities above (no external source of truth, per `design/integration.md`).
- Retention/deletion policy (UK GDPR requirement): student records (including consent, medical/dietary/SEN data) retained **3 years from the student leaving/graduating** the programme. DBS (Disclosure and Barring Service, gov.uk) checks are held against Teachers, not students, and are retained **6 years** after expiry/the teacher leaving.
- **Deletion enforcement — interim decision:** manual, Admin-triggered deletion once a record passes its retention period (an Admin-facing "records due for deletion" report from `ConsentExpiryJob`'s wider sweep). Full automated purge is deferred — acceptable for solo-scale operation, but recorded here as a deliberate interim choice, not an oversight (per design review).
- No migration — clean start (confirmed, `raid.md` A-003).
