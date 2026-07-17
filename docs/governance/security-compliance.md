# Security & Compliance Assessment

Status: in progress

## Data classification drivers (confirmed in scope)

- The system manages safeguarding/consent data directly: medical information, photo/media consent, dietary/SEN data, DBS (Disclosure and Barring Service) check records, emergency contact details. This is special-category and children's personal data.
- Implication: UK GDPR / Data Protection Act 2018 obligations apply directly (lawful basis, special-category conditions, retention/deletion, data subject rights, likely a DPIA). To be confirmed with Microsoft Learn / ICO guidance during design, not assumed.
- Column-level access control / masking on sensitive fields, and audit logging of access, was a hard requirement in the prior build — carried forward as a hypothesis for NFR-nnn, to be confirmed.
- Confirmed: no external governing body/regulator (e.g. diocese, national youth-org policy) imposes rules beyond general UK law — general UK GDPR/DPA 2018 and safeguarding good practice is the applicable bar.

## Access control by role (confirmed)

- `DbsCheck` records — whole-record access restricted to **Admin and Safeguarding Officer only**. Teacher and Approver have no access, not even a masked view.
- `ConsentRecord` sensitive fields (medical, dietary/SEN, emergency contact) — masked from Teacher and Approver; full visibility for Admin and Safeguarding Officer.
- The **Approver** role is scoped to curriculum/lesson-change approval only — it carries no safeguarding, consent, or DBS privileges. This was a discovery correction: the Approver role must not be conflated with consent/safeguarding sign-off.
- Consent record approval (`ConsentRecord.ApprovedByUserId`) is performed by Safeguarding Officer or Admin, not by the Approver role.

## Retention (confirmed)

- Student records (including consent, medical/dietary/SEN data): retained **3 years from the student leaving/graduating** the programme.
- `DbsCheck` records: retained **6 years** after expiry or the teacher leaving.
- Deletion mechanism (hard delete vs anonymisation) and enforcement (manual admin process vs automated purge job): [TBC — design detail, not yet decided].
