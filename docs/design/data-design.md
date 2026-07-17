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
| `StudentGuardianLink` | StudentId, ParentGuardianId | Many-to-many: a parent may have several children; a student may have more than one guardian. |
| `StudentLessonCompletion` | Id, StudentId, LessonId, CompletedByUserId, CompletedAt, Note (optional), IsReversed, ReversedAt | Teacher marks and can self-correct (`IsReversed`/`ReversedAt`) — no separate Admin correction gate. |
| `Certificate` | Id, StudentLessonCompletionId, GeneratedAt, BlobKey (PDF) | Auto-generated on completion (QuestPDF). Real tracked record, not implicit. |
| `RankBadge` | Id, RankId, ImageBlobKey | One badge per rank. |
| `StudentBadge` | Id, StudentId, RankBadgeId, AwardedAt | Awarded on promotion. Real tracked record, not implicit. |
| `StudentRankProgress` | Id, StudentId, RankId, StartedAt, CompletedAt | Promotion history; a new row starts when a student enters a rank, closes when they complete it (auto-promotion trigger). |
| `ConsentRecord` | Id, StudentId, PeriodStart, PeriodEnd (annual), PhotoMediaConsent, EmergencyMedicalInfo\*, DietarySEN\*, TransportOffsiteConsent, DataSharingConsent, EmergencyContact\*, Status (Pending/Approved/Rejected), SubmittedByParentId, ApprovedByUserId (Safeguarding Officer or Admin — **not** the Approver role, which is curriculum-only), ExpiryDate | \* = sensitive, column-level masked (see below). Annual renewal. Expiry blocks `StudentLessonCompletion` creation for that student. |
| `DbsCheck` | Id, TeacherUserId, CheckDate, ExpiryDate, CertificateNumber\*, Status | \* = sensitive. **Access restricted entirely to Safeguarding Officer and Admin** — Teacher and Approver have no access at all (not just masked fields; the whole record is out of scope for those roles). |
| `AppUser` | Id, EntraObjectId, DisplayName, Email | Maps to the Entra External ID identity. |
| `UserRole` | UserId, Role | Role ∈ {Admin, Teacher, Approver, Parent, Student, SafeguardingOfficer}. A single user may hold more than one role (e.g. a Teacher who is also the Approver). |

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
- Enforcement mechanism (EF Core query filters vs a data-access-layer policy check vs column encryption): [TBC — low-level-design.md].

## Data ownership & lifecycle

- System of record: this database is authoritative for all entities above (no external source of truth, per `design/integration.md`).
- Retention/deletion policy (UK GDPR requirement): student records (including consent, medical/dietary/SEN data) retained **3 years from the student leaving/graduating** the programme. DBS (Disclosure and Barring Service, gov.uk) checks are held against Teachers, not students, and are retained **6 years** after expiry/the teacher leaving.
- No migration — clean start (confirmed, `raid.md` A-003).
