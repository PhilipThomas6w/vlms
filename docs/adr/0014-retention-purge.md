# ADR-0014: Retention purge

Status: **Accepted** (Philip, 27/10/2026). Resolves STATE decision 2 (purge approach) and DPIA residual risk 7. Amends `design/data-design.md` and `governance/security-compliance.md` ("Deletion enforcement — interim decision"); builds on `0004` pass 8 (row-level security on `sensitive_data_access_logs`) and `0010` (chapters).

## Context

Retention is confirmed (`governance/security-compliance.md`): students 3 years after leaving or graduating, DBS records 6 years after expiry or the teacher leaving, registration applications 1 year after the decision, the read-audit log (`SensitiveDataAccessLog`) 6 years; hard delete, not anonymisation. The "records due for deletion" report flags students, DBS records and applications, but nothing deletes them. Since `0004` pass 8, the app's own database login (`vlms_app`) cannot delete from `sensitive_data_access_logs` under any circumstance, by design, so the audit log cannot be purged by the app either.

## Decision

1. **Children's and staff records: an Admin confirms each deletion.** The "records due for deletion" report gains "Delete" beside each record and "Delete all shown". Each deletion is confirmed on the page by typing the person's name, because it cannot be undone. An Admin deletes records of their active chapter only (`0010`). No record is deleted automatically: a wrong leaving date would otherwise silently destroy a child's record.
2. **What a student's deletion removes:** the student and everything linked to them: completions, certificates, attendance, consent records (with their sensitive details), guardian links and the child's sign-in account. A DBS record's deletion removes that record. An application's deletion removes it with its restricted details.
3. **Parent accounts** are deleted with their last linked child, unless the parent holds a staff role or still has another child in the programme.
4. **Deletion log.** Each deletion writes a permanent row holding only the record type, its ID, the date and who deleted it, with no personal data. It is the evidence that retention is enforced.
5. **Monthly reminder.** A scheduled job emails the chapter's Admins once a month while any of its records are due, so deletion does not depend on someone opening the report.
6. **The read-audit log is purged automatically**, monthly, by a scheduled job that deletes rows older than 6 years. It connects as a dedicated database role whose only row-level-security `DELETE` policy on `sensitive_data_access_logs` is limited to rows older than 6 years; this is narrower than `BYPASSRLS`, and `vlms_app` still cannot delete from the table at all. No confirmation is needed: it removes only expired log rows, never a person's record.
7. **Backups** keep deleted data until they expire under the off-box backup lifecycle (the period is an open ops-pack item); the privacy notice says so.

## Consequences

- DPIA risk 7 falls from Medium once built: retention is enforced by a monthly reminder and a human confirmation, and the audit log purges itself.
- One new entity (the deletion log) and one EF migration; a new database role and RLS policy in a PostgreSQL-only migration, with its provisioning added to `deploy/README.md` (roles and grants) and the role's credentials held only by the jobs service.
- Deletion code is security-critical (it crosses the `0004` whole-entity restrictions on `DbsCheck` and `ConsentSensitiveDetails`), so it is enforced in the service layer, audited, and re-reviewed in the ASVS checklist.
- Deletion is manual, so a record can stay past its date if Admins ignore the reminder. Accepted at this scale.
