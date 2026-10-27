# ADR-0014: Retention purge

Status: **Accepted** (Philip, 27/10/2026). Resolves STATE decision 2 (purge approach) and DPIA residual risk 7. Amends `design/data-design.md` and `governance/security-compliance.md` ("Deletion enforcement — interim decision"); builds on `0004` pass 8 (row-level security on `sensitive_data_access_logs`) and `0010` (chapters).

## Context

Retention is confirmed (`governance/security-compliance.md`): students 3 years after leaving or graduating, DBS records 6 years after expiry or the teacher leaving, registration applications 1 year after the decision, the read-audit log (`SensitiveDataAccessLog`) 6 years; hard delete, not anonymisation. The "records due for deletion" report flags students, DBS records and applications, but nothing deletes them. Since `0004` pass 8, the app's own database login (`vlms_app`) cannot delete from `sensitive_data_access_logs` under any circumstance, by design, so the audit log cannot be purged by the app either.

## Decision

1. **Children's and staff records: an Admin confirms each deletion.** The "records due for deletion" report gains "Delete" beside each record and "Delete all shown". Each deletion is confirmed on the page by typing the person's name, because it cannot be undone. An Admin deletes records of their active chapter only (`0010`). No record is deleted automatically: a wrong leaving date would otherwise silently destroy a child's record.
2. **What a student's deletion removes:** the student and everything linked to them: completions, certificates, attendance, consent records (with their sensitive details), guardian links and the child's sign-in account. A DBS record's deletion removes that record. An application's deletion removes it with its restricted details.
3. **Parent accounts** are deleted with their last linked child, unless the parent holds a current staff role or still has another child in the programme. **Staff accounts** (Philip, 27/10/2026; wording amended 27/10/2026 with FR-017): the account of staff who have stepped down is deleted once their DBS record has been deleted (6 years after stepping down, counted from the current step-down date) and they have no child still in the programme; the staff roles kept for reinstatement do not count as current. Reinstating stops the clock; stepping down again restarts it.
4. **Deletion log.** Each deletion writes a permanent row holding only the record type, its ID, the date and who deleted it, with no personal data. It is the evidence that retention is enforced.
5. **Monthly reminder.** A scheduled job emails the chapter's Admins once a month while any of its records are due, so deletion does not depend on someone opening the report.
6. **The read-audit log is purged automatically**, monthly, by a scheduled job that deletes rows older than 6 years. It connects as a dedicated database role whose only row-level-security `DELETE` policy on `sensitive_data_access_logs` is limited to rows older than 6 years; this is narrower than `BYPASSRLS`, and `vlms_app` still cannot delete from the table at all. No confirmation is needed: it removes only expired log rows, never a person's record.
7. **Backups** keep deleted data until they expire under the off-box backup lifecycle (the period is an open ops-pack item); the privacy notice says so.

## Consequences

- DPIA risk 7 falls from Medium once built: retention is enforced by a monthly reminder and a human confirmation, and the audit log purges itself.
- One new entity (the deletion log) and one EF migration; a new database role and RLS policy in a PostgreSQL-only migration, with its provisioning added to `deploy/README.md` (roles and grants) and the role's credentials held only by the jobs service.
- Deletion code is security-critical (it crosses the `0004` whole-entity restrictions on `DbsCheck` and `ConsentSensitiveDetails`), so it is enforced in the service layer, audited, and re-reviewed in the ASVS checklist.
- Deletion is manual, so a record can stay past its date if Admins ignore the reminder. Accepted at this scale.

## Built (27/10/2026)

`Retention/RetentionDeletionService` (Delete and Delete all shown on the report), `RetentionReminderService` and `AuditLogPurgeService`, run monthly by `Vlms.Jobs retention-monthly` (`deploy/systemd/vlms-retention.*`); entity `DeletionLogEntry` and migration `AddDeletionLog` (append-only deletion log; the `vlms_retention` DELETE policy); provisioning in `deploy/README.md` step 3b. Readings made while building, for confirmation:

- An account is also kept while it holds an Approver or national role or any role in another chapter, and while it has a registration application on record. (As first built it was also kept while any other kept record still named it; superseded below, "References to a deleted account".)
- A stepped-down staff member's account goes only once they stepped down at least 6 years ago (current step-down date), whichever record's deletion triggers the check.
- Deleting an application does not delete the applicant's account (this ADR names no rule for it).
- "Delete all shown" asks for each person's name, one field per record.
- The audit-log purge writes no deletion-log row (it removes log rows, not a person's record); its count is in the job's journal.

## Settled after build (Philip, 27/10/2026)

- **References to a deleted account:** when an account is deleted, records that name it (a register taken by, a lesson version suggested or approved by, and similar) keep their content and show "a former member of staff" instead of the person; only the link to the person goes. This replaces the as-built rule that an account named by a kept record is never deleted. Build item in `STATE.md`.
  - **Built (27/10/2026):** migration `ClearDeletedAccountReferences`. Every foreign key to an account that names the person only as the one who acted is nullable with `ON DELETE SET NULL`, so the database clears it inside the deletion's own transaction (chosen over clearing each in `RetentionDeletionService`: it needs no list of tables in the service, and a reference added later must pick a delete behaviour, which a test checks). They are: register taken by; completion recorded by; event created by; guardian link and guardian invite created by, invite used by; consent form entered by and approved by; lesson suggestion author, decided by and referred by; committee approval by; lesson version suggested, approved and published by; publishing setting changed by; step-down recorded by and reinstated by; application reviewed by and decided by; student's assigned teacher; added saint added by. The references about the person are unchanged: their roles, preferences, class-teacher rows and step-down rows go with the account, and a DBS check, student record, guardian row with a child or application still keeps it. `RetentionDeletionService` no longer keeps an account because another record names it; every other keep-rule above stands.
  - The pages show "a former member of staff" only where the record says someone acted: a register, completion, link, author, committee approval or setting change always names someone; a decision, review, referral or reinstatement names someone once its date is set; a lesson version other than a converted one names who suggested it and who approved or published it (new column `lesson_versions.published_directly` says which); a step-down names who recorded it unless it was copied in before the history existed (new column `staff_step_downs.recorded_before_history`, filled by the migration). A student whose assigned teacher's account was deleted shows no assigned teacher. The append-only read-audit and deletion logs hold the account id as a plain value with no foreign key, so nothing needed an UPDATE their row-level security forbids.
- Confirmed as built: deleting a registration application does not delete the applicant's account; Approver and national roles keep an account; the audit-log purge writes only a count to the job's journal, not deletion-log rows.
