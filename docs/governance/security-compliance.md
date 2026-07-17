# Security & Compliance Assessment

Status: in progress

## Data classification drivers (confirmed in scope)

- The system manages safeguarding/consent data directly: medical information, photo/media consent, dietary/SEN data, DBS (Disclosure and Barring Service) check records, emergency contact details. This is special-category and children's personal data.
- Implication: UK GDPR / Data Protection Act 2018 obligations apply directly (lawful basis, special-category conditions, retention/deletion, data subject rights, likely a DPIA). **DPIA screening is tracked directly in `governance/raid.md` (D-003), owned by Philip, due before go-live** — not left as a forward reference to a document that doesn't exist yet.
- Whole-entity access restriction on sensitive data, and **audit logging of reads** (not just writes) — confirmed as NFR-001 (`requirements/non-functional.md`), enforced via the `ConsentRecord`/`ConsentSensitiveDetails` entity split + EF Core global query filters + a `DbCommandInterceptor`-based `SensitiveDataAccessLog` (`adr/0004-sensitive-data-access-control.md`, `design/data-design.md`).
- Confirmed: no external governing body/regulator (e.g. diocese, national youth-org policy) imposes rules beyond general UK law — general UK GDPR/DPA 2018 and safeguarding good practice is the applicable bar.

## Access control by role (confirmed)

- `DbsCheck` records — whole-record access restricted to **Admin and Safeguarding Officer only**. Teacher and Approver have no access, not even a masked view.
- `ConsentSensitiveDetails` (medical, dietary/SEN, emergency contact — split out from `ConsentRecord` at design review, since these can't be column-masked on a row Teacher otherwise needs) — visible only to Admin and Safeguarding Officer. `ConsentRecord` itself (status/expiry) remains readable by Teacher/Approver.
- The **Approver** role is scoped to curriculum/lesson-change approval only — it carries no safeguarding, consent, or DBS privileges. This was a discovery correction: the Approver role must not be conflated with consent/safeguarding sign-off.
- Consent record approval (`ConsentRecord.ApprovedByUserId`) is performed by Safeguarding Officer or Admin, not by the Approver role.

## Retention (confirmed)

- Student records (including consent, medical/dietary/SEN data): retained **3 years from the student leaving/graduating** the programme.
- `DbsCheck` records: retained **6 years** after expiry or the teacher leaving.
- Deletion enforcement — interim decision (`design/data-design.md`): manual, Admin-triggered deletion via a "records due for deletion" report, once a record passes its retention period. Full automated purge deferred as an acceptable trade-off at solo-maintainer/tens-of-users scale.
- Hard delete vs anonymisation — **decided:** hard delete (`design/data-design.md`), to avoid the anonymisation-adequacy question and because nothing in scope needs retained-but-anonymised historical data.
- `SensitiveDataAccessLog` (the audit trail itself) is retained **6 years** independent of the referenced record's own retention, and is tamper-protected at the database permission level (`UPDATE`/`DELETE` denied), not just an application convention.
- Guardian-link verification (`design/data-design.md`): a `StudentGuardianLink` is only ever created by Admin/Teacher at student registration, never by parent self-service — closes a gap where Entra External ID's self-service sign-up would otherwise let a parent claim an unverified relationship to a child before accessing their medical/consent data. The Admin/Teacher verifies this through the same enrolment channel already used for the student, not a separate formal process — proportionate to a small, personally-known membership.
