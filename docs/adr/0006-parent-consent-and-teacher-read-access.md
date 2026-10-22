# 0006 - Parent-submitted consent, and Teacher/linked-Parent access to `ConsentSensitiveDetails`

## Status

Accepted (Philip, 22/10/2026). Amended the same day (Philip, 22/10/2026): Teachers must have TOTP MFA enabled to read `ConsentSensitiveDetails`, and MFA is now enforced for the Teacher role on every policy, as it is for Admin and Safeguarding Officer — see "Amendment 22/10/2026: MFA for Teachers" below. Amends `adr/0004-sensitive-data-access-control.md` Decision §2 for `ConsentSensitiveDetails` only. Everything else in ADR-0004 stands: the entity split, the query-filter mechanism, the `IMaterializationInterceptor` read audit, the MFA conjunct for Admin/Safeguarding Officer, and the tamper protection and Row-Level Security on `sensitive_data_access_logs`. `DbsCheck` is unchanged.

**DPIA review outstanding.** Parents now enter, and Teachers now read, special-category data about children. `governance/dpia.md` must be reviewed and signed off by Philip before go-live. This ADR does not do that.

## Context

ADR-0004 restricted `ConsentSensitiveDetails` (emergency medical information, dietary/SEN needs, emergency contact) to Admin and Safeguarding Officer. Consent was entered by those two roles "on behalf of" the parent, from a paper form.

Walking the user journeys on 22/10/2026 (`requirements/user-journeys.md` J5 step 6) showed this does not fit how the programme works:

- Parents hold the information and should give it directly, each year, for each child (`requirements/functional.md` FR-015).
- Teachers run the sessions. If a child has an allergy, asthma or a special educational need, the teacher in the room needs to know it, and needs the emergency contact. Under ADR-0004 they could not see it.
- A student's name and date of birth should come from the parent's form, not be retyped by staff (FR-013).

Philip decided (22/10/2026):

1. Parents complete and submit the annual consent form in the app for each child they are linked to (`StudentGuardianLink`, Parent role, not left). A Safeguarding Officer or Admin approves it, or rejects it with comments; the parent sees the status and can resubmit.
2. `ConsentSensitiveDetails`: linked parents can view and update their own child's details; teachers can view them; Admin and Safeguarding Officer as before. Every read stays audit-logged. `DbsCheck` is unchanged. The Approver role gets nothing.
3. An approved submission updates the `Student` record's personal details.

## Decision

### 1. Query filter on `ConsentSensitiveDetails`

`VlmsDbContext.OnModelCreating` now reads:

```csharp
e.HasQueryFilter(x =>
    ((_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
        && _currentUser.HasMfaEnabled)
    || (_currentUser.HasRole(Role.Teacher) && _currentUser.HasMfaEnabled)
    || (_currentUser.HasRole(Role.Parent)
        && _currentUser.UserId != null
        && StudentGuardianLinks.Any(link =>
            link.StudentId == x.ConsentRecord!.StudentId
            && link.ParentGuardian!.AppUserId == _currentUser.UserId
            && !link.ParentGuardian.AppUser!.HasLeft)));
```

Three arms, any one of which admits a row:

- **Admin / Safeguarding Officer with MFA** — unchanged from ADR-0004 pass 7.
- **Teacher with MFA** — every row (the MFA conjunct was added by the amendment below). Teachers see all students (`Student.AssignedTeacherUserId` is informational only; FR-009), so there is no per-student scoping to apply.
- **Parent** — only rows whose `ConsentRecord.StudentId` is linked, through a `StudentGuardianLink`, to a `ParentGuardian` whose `AppUserId` is the caller, and only while the caller's `AppUser.HasLeft` is false.

Two conjuncts in the Parent arm both protect the same edge case, and both are kept on purpose. With no resolved `UserId`, EF Core's C# null semantics would turn `AppUserId == UserId` into `AppUserId IS NULL` and match every guardian who has no account yet. `UserId != null` stops that directly. `!HasLeft` also stops it, because a guardian with no account has no joined `AppUser`, so the condition is not true. Break-testing confirmed each alone is enough (see `governance/asvs-access-control-checklist.md`, 22/10/2026 row); keeping both means neither is a single point of failure.

EF Core supports `DbSet` access and navigations inside a query filter (`QueryFilterRewritingConvention`; [Global Query Filters](https://learn.microsoft.com/ef/core/querying/filters)). The filter references `StudentGuardianLinks`, `ParentGuardian` and `AppUser`, none of which has a filter of its own, so there is no filter cycle (the documented limitation on that page).

The Approver and Student roles match no arm. A user holding several roles is admitted if any arm matches (for example, an Admin without MFA who is also a Parent reads their own linked children's rows through the Parent arm). Since the amendment below, an Admin without MFA who is also a Teacher no longer reads through the Teacher arm.

`DbsCheck`'s filter is byte-for-byte unchanged: Admin or Safeguarding Officer, with MFA.

### 2. Writes are checked in the service, not the filter

A query filter restricts reads only. EF Core does not apply it to `Add`. So:

- `ParentConsentService.SubmitAsync` requires `Role.Parent`, a resolved `UserId`, `HasLeft == false`, and a `StudentGuardianLink` from the caller's own `ParentGuardian` to the target student before it adds anything.
- Teachers have no write path. `StudentConsentViewService` is read-only, and `ConsentRecordService` still requires Admin or Safeguarding Officer.
- Only `ConsentRecordService.DecideAsync` (Admin or Safeguarding Officer) changes a record's status.

### 3. Submissions are append-only history

Each submission is a new `ConsentRecord` (status Pending) with its own `ConsentSensitiveDetails` row. Nothing a parent does updates or deletes an existing row:

- A resubmission after rejection is a new row; the rejected one stays with its comments.
- An update after approval is a new Pending row. The approved record stays Approved and keeps satisfying the FR-003 completion block until the new one is decided.
- While a submission is Pending, a second one for the same child is refused, so the reviewer never has two versions to choose between.

`ConsentRecord` gains (migration `AddParentConsentSubmissionFields`, nullable columns only): `SubmittedAt`, `SubmittedByUserId` (the parent, or the staff member entering a paper form), `StudentName`, `StudentDateOfBirth`, `DecidedAt`, `DecisionComments`.

A parent submission covers one year from the day it is submitted (`PeriodStart` = that day, `PeriodEnd` = `ExpiryDate` = a year later less a day). Approval does not move it.

A `ConsentSensitiveDetails` row now takes its `ConsentRecord`'s id. The previous `MAX(id) + 1` over `ConsentSensitiveDetails` runs through the caller's query filter; a parent sees only their own children's rows, so it would hand out ids already in use. `ConsentRecord` has no filter, and the two are strictly 1:1.

### 4. Decision and the `Student` update

`ConsentRecordService.DecideAsync(id, approve, comments)`:

- Admin or Safeguarding Officer only; never the Approver.
- Only a Pending record can be decided.
- Rejection requires non-blank comments, which the parent sees.
- Approval applies `StudentName` and `StudentDateOfBirth`, when the form carried them, to the `Student` in the same `SaveChangesAsync`. It never changes the student's status, rank or assigned teacher (FR-013: only Admins do that). Values are set property by property through `Entry(...).CurrentValues[...]`, because the entities are init-only, so columns added later are not reset.

The Admin/Safeguarding Officer "paper form" path (`RecordAsync`) is kept. Its records carry no name or date of birth, so approving one leaves the `Student` as it is.

### 5. Read audit

Unchanged mechanism: `SensitiveDataAuditInterceptor` writes a `SensitiveDataAccessLog` row for every `ConsentSensitiveDetails` entity materialized, with the caller's own `UserId`, so a Teacher's or Parent's read is attributed to them. The new read paths (`ParentConsentService.GetMyChildrenAsync`, `StudentConsentViewService.GetStudentAsync`) materialize whole entities before mapping them. The parent page reads only the latest submission's details per child; the teacher page reads only the current approved submission's details for the student opened. The teacher list page reads no sensitive data.

### 6. Pages

| Page | Policy | What it does |
|---|---|---|
| `/parent/consent` | `RequireParent` | Lists linked children with consent status; form to submit or resubmit |
| `/students/consent`, `/students/{id}/consent` | `RequireTeacher` | Read-only status list; one student's approved consent and details |
| `/safeguarding/consent` | `RequireAdminOrSafeguardingOfficer` | Pending submissions; approve, or reject with comments; paper-form entry |

Teachers see only details from an approved submission, not a pending one, so what they act on has been reviewed.

## What stays unchanged

- `DbsCheck`: Admin or Safeguarding Officer with MFA only.
- The Approver role: curriculum only, no consent or safeguarding access.
- `StudentGuardianLink` creation: Admin or Teacher only, never parent self-service (FR-004). Parent access depends on it.
- `ConsentRecord` itself has no query filter (status and expiry are needed by Teachers for FR-003, and now by Parents for their own page).
- No `IgnoreQueryFilters()` anywhere (the gate checks this).
- Tamper protection and Row-Level Security on `sensitive_data_access_logs` (ADR-0004 §4, §6). The migration adds columns to an existing table only, so `deploy/postgres/10-grant-app-privileges.sql`'s grants are unaffected.

## Amendment 22/10/2026: MFA for Teachers

Philip decided (22/10/2026) to close risk 1 below: Teachers must use TOTP MFA.

- **Data layer:** the Teacher arm of the `ConsentSensitiveDetails` query filter carries `&& _currentUser.HasMfaEnabled` (§1 above shows the current text). A Teacher without MFA reads no rows through any code path.
- **Page layer:** `MfaRequirementHandler` treats `Role.Teacher` as MFA-gated alongside Admin and Safeguarding Officer. Because `MfaRequirement` rides every policy in `Program.cs`, a Teacher without MFA is refused every policy-gated page and the lesson-file endpoint, not just `/students/consent`. This is the same "globally" rule ADR-0004 pass 7 applies to Admin and Safeguarding Officer (`MfaRequirement`'s doc comment).
- **Enrolment path:** `Account/Manage/TwoFactorAuthentication` and `Account/Manage/EnableAuthenticator` carry plain `[Authorize]` (the default policy, no `MfaRequirement`), so a Teacher without MFA can still reach them and enrol. The page now shows the "your role requires two-factor authentication" warning to Teachers too. The router's not-authorised message links to the enrolment page. The login flow does not redirect a non-MFA user there automatically; the nav bar link and the not-authorised message are the route in.
- **Unchanged:** Parents, Students and Approvers are offered MFA but never required to use it; the Parent arm of the filter has no MFA conjunct. `DbsCheck` is unchanged. No migration.
- **Operational consequence:** an existing Teacher account without MFA loses access to every Teacher page at deployment until the teacher enrols. Teachers should be told before go-live.

Tests: `MfaRequirementHandlerTests` (Teacher without MFA fails, with MFA passes; Parent, Student and Approver pass without MFA), `SensitiveDataAccessControlTests` (Teacher without MFA reads no details; with MFA reads all; Parent without MFA still reads own child's), `LessonFileEndpointTests` (Teacher without MFA refused through the real policy pipeline; Parent without MFA allowed).

## Risks and open questions (for Philip)

1. **Teachers read special-category data without MFA.** **Closed 22/10/2026** by the amendment above: MFA is now required for Teachers, in the filter and on every policy. (Originally: MFA was enforced only for Admin and Safeguarding Officer, so a teacher's account was a route to medical and SEN information for every student.)
2. **Every teacher sees every student's details**, not only their own students. This follows FR-009 (teachers cover for each other) but is wider than strictly necessary. Recorded for the DPIA.
3. **Projections bypass the read audit.** `IMaterializationInterceptor` fires only for materialized entities. A future `.Select(d => d.EmergencyMedicalInfo)` would read the data without an audit row. This was already true under ADR-0004 and stays a manual review item in the ASVS checklist (V16).
4. **Parents enter the data themselves.** Accuracy now depends on the parent; the reviewer sees the full submission before approving.
5. **Data minimisation:** a parent sees only the latest submission's details for each of their own linked children, never another family's. Two guardians linked to the same child both see that child's details, including what the other entered.
6. **DPIA**: must be reviewed (see `governance/dpia.md`). Not signed off here.

## Verification

Tests (SQLite in-memory; see `openwiki/testing.md` for why that is the project's provider strategy):

- `SensitiveDataAccessControlTests` — linked parent reads only own child; another child's parent, an unlinked parent, a parent who has left, and a Parent-role caller with no resolved id read nothing; Teacher with MFA reads all details, Teacher without MFA reads none (amendment), neither reads `DbsCheck`; Approver reads neither; Safeguarding Officer unchanged; Teacher and Parent reads each write audit rows attributed to the caller.
- `ParentConsentServiceTests` — submit and read for own child; another child's parent can neither; non-parents cannot use the form; teacher reads (audited) but cannot write or decide; Approver reads nothing; approval updates the student and lifts the `CompletionService` consent block; rejection keeps the student unchanged and shows comments; resubmission after rejection; second submission while pending refused; update after approval keeps the approved record current; id assignment does not collide with paper-form records.
- `ConsentRecordServiceTests` — rejection needs comments; only Pending can be decided; approving a paper-form record leaves the student unchanged.

Break tests (recorded in the ASVS checklist): widening the Parent arm to any link failed six tests; skipping the audit for Teacher reads failed two.

**Not exercised:** the filter's generated SQL against a live PostgreSQL server, and the migration applied to one. No local PostgreSQL is available. The pages have not been run in a browser.

## Alternatives considered

- **Keep Admin/Safeguarding Officer only, and have staff print medical details for teachers.** Rejected by Philip: paper copies are harder to keep current and are not audited at all.
- **A separate, teacher-visible subset of the sensitive fields** (for example, medical and dietary but not emergency contact). Would need another entity split. Not asked for; teachers need the emergency contact too.
- **Let parents edit the approved record in place.** Rejected: it would change what a Safeguarding Officer approved without review, and lose the history of what was consented to.
- **Enforce parent writes only through the page.** Rejected for the same reason ADR-0004 rejected page-only checks: a future caller could bypass it. The check is in the service.
