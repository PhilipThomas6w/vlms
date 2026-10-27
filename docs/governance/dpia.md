# Data Protection Impact Assessment (draft)

Status: **approved by Philip, 27/10/2026** (lawful bases, conditions, providers, rights, residual risks and sign-off confirmed; section 6). First written 13/10/2026 to discharge `governance/raid.md` D-003 (owner: Philip, due before go-live). Structure follows the ICO's DPIA steps ([ICO: Data protection impact assessments](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/accountability-and-governance/data-protection-impact-assessments-dpias/)). This draft states what the system does and what the design already controls. **It does not decide legal questions:** lawful bases and conditions are proposed for Philip to confirm, with advice if needed, and were confirmed by Philip on 27/10/2026. Nothing here is legal advice.


> **REVIEW NEEDED — access change of 22/10/2026, not yet assessed here (Philip).** `adr/0006-parent-consent-and-teacher-read-access.md` (functional.md FR-013, FR-015) changes who handles the special category data in this assessment, and this DPIA has **not** been updated or re-signed for it:
>
> - **Parents now enter** the annual consent form in the app, including emergency medical information, dietary/SEN needs and emergency contact, for each child linked to them. A Safeguarding Officer or Admin approves or rejects each submission. Approval also updates the child's name and date of birth.
> - **Teachers can now read** `ConsentSensitiveDetails` for every student (read only), **with MFA required** (amended 22/10/2026, Philip: a Teacher without TOTP MFA reads none of it and is refused every Teacher page until they enrol; adr/0006 "Amendment 22/10/2026"). **Linked parents** can read and update their own children's details. Admin and Safeguarding Officer unchanged. Every read is still audit-logged, attributed to the reader. `DbsCheck` is unchanged.
> - The "Who can see it" column in section 2's table, and the risks in section 5, describe the earlier Admin/Safeguarding Officer-only position and are now out of date for `ConsentSensitiveDetails`.
>
> Points for the review (from adr/0006 "Risks"): ~~whether teachers should need MFA~~ (decided 22/10/2026: yes, now enforced); whether all teachers, rather than a child's own teachers, need the details; necessity and proportionality of the wider access; how parents are told who will see what they enter (privacy information on the form). Nothing in this note is a sign-off.

> **REVIEW NEEDED — parent sign-up of 23/10/2026, not yet assessed here (Philip).** `adr/0008` (functional.md FR-004 as amended 22/10/2026, FR-019) lets any adult create an account and apply to register their children. This DPIA has **not** been updated or re-signed for it:
>
> - **Special category data arrives before any staff check.** An applicant enters each child's emergency medical information, dietary requirements, special educational needs and emergency contact before anyone at the club has confirmed who they are. Until now a parent could enter these only for a child an Admin had already linked to them. A false applicant can therefore put data about a real child, or a made-up one, into the system.
> - **New restricted store.** The data is held in `StudentApplicationSensitiveDetails`. It is visible only to Admins and Safeguarding Officers with MFA and to the applicant, never to Teachers, Approvers or other parents, and every read is audit-logged. The approved details are copied into `ConsentSensitiveDetails`, and the application row is kept as the record of what was reviewed.
> - **New data about parents:** phone number and postal address (`StudentApplication`), seen by Admins and Safeguarding Officers on the review pages and by the applicant.
> - **Review exchange.** The Safeguarding Officer's question, the parent's answer and the decision comments are free text and may contain special category data, so they are in the restricted store. They are never emailed, and the emails only say "sign in to read" (registration choice 1 in `STATE.md`). On resubmission the details are overwritten in place, so earlier versions are not kept (choice 4).
> - **Retention.** Approved and rejected applications, with their restricted details, are kept 1 year after the decision (Philip, 23/10/2026; `security-compliance.md`) and are then flagged on the "records due for deletion" report, where an Admin deletes them (risk 7, `adr/0014`).
> - **Controls:** nothing is created from an application (no student, link, consent or Parent role) until a Safeguarding Officer approves stage 1 and an Admin approves stage 2. The Safeguarding Officer's alert email carries no sensitive details. Tested in TC-039, TC-040 and TC-041.
>
> Points for the review: the lawful basis and Article 9 condition for data entered by an unverified applicant; privacy information on the apply form; whether the applicant should be told when their application is rejected without reasons. Nothing in this note is a sign-off.

## 1. Screening: is a DPIA needed?

The ICO says a DPIA is required for processing likely to result in high risk, and lists examples ([ICO: examples of processing likely to result in high risk](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/accountability-and-governance/data-protection-impact-assessments-dpias/examples-of-processing-likely-to-result-in-high-risk/)). Relevant factors here:

- children's personal data (the students), whom the ICO treats as vulnerable;
- special category data (health: emergency medical information, dietary and SEN needs);
- criminal offence data (DBS check records for adult volunteers);
- a decision affecting access to an activity (expired consent blocks lesson completion), although this is a rule applied to a record the club holds, not profiling.

The processing is small in scale (tens of people per role) and is not profiling, marketing or a service offered to children online. Several criteria still apply together, and a breach could cause physical or safeguarding harm (e.g. a child's medical needs or a volunteer's DBS status exposed). **Screening outcome: carry out a full DPIA** (confirmed, Philip 27/10/2026). This document is that DPIA.

## 2. The processing

**Controller:** Shrewsbury Orthodox Church, a charitable incorporated organisation (CIO) registered with the Charity Commission for England and Wales, charity no. 1166735 (Safeguarding Policy v4, adopted 14/10/2026). A CIO has its own legal personality, so the CIO is the controller and its trustees act for it ([ICO: what are controllers and processors](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/controllers-and-processors/controllers-and-processors/what-are-controllers-and-processors/)). Philip Thomas (Secretary and Safeguarding Trustee, web administrator, and a Teacher) is responsible for data protection on the trustees' behalf (Philip, 23/10/2026; Philip stated "I am the data controller", wording to confirm against the CIO position above). Varangians is the church's youth club.

**Purpose:** run the youth programme: track lesson completions and rank progression, issue certificates and badges, manage the curriculum, hold annual parental consent and volunteers' DBS checks, keep parents informed, and report on progress and disengagement.

| Data subjects | Data | Source | Who can see it |
|---|---|---|---|
| Students (children) | name, date of birth, enrolment date, rank, lesson completions, badges, certificates, status, leaving date | Teacher/Admin at enrolment; Teachers during sessions | Teachers, Admin; own parent(s); Approver sees curriculum only |
| Students | annual consent: photo/media, transport/off-site, data sharing; status and expiry | Parent (paper or form), recorded by Admin/SO | Teacher, Approver, Admin, SO; parent |
| Students | **emergency medical information, dietary/SEN needs, emergency contact** (`ConsentSensitiveDetails`) | Parent | **Admin and Safeguarding Officer only**, with MFA; every read audit-logged |
| Parents/guardians | name, contact details, link to child, account email | Admin/Teacher at enrolment; parent on accepting invite | Admin, Teachers |
| Volunteers (teachers) | name, email, roles, leaving flag | Admin | Admin |
| Volunteers | **DBS check: date, expiry, certificate number, status** (`DbsCheck`) | Admin/SO from the DBS certificate | **Admin and Safeguarding Officer only**, with MFA; every read audit-logged |
| All users | sign-in data: email, password hash, MFA secret, lockout state; access log of sensitive reads | the system | Admin (audit log); SO |

Volume: tens of students, parents and volunteers (`governance/raid.md` A-002).

**Not collected:** religion is not recorded as a field, although membership of a church youth club may itself reveal religious belief. Membership is treated as special category data (Philip, 27/10/2026), under the Art 9(2)(d) condition below.. No photos are stored. No DBS certificate images are stored, only the certificate number and dates.

**Processors and locations:**

| Processor | What | Location |
|---|---|---|
| DigitalOcean | VPS hosting: the database and files | London (LON1) |
| Cloudflare (R2) | encrypted-in-transit off-box backups: database dumps and certificate PDFs | EU jurisdiction bucket (Philip, 27/10/2026); the UK's adequacy regulations cover the EU, so this is not a restricted transfer |
| Microsoft (the church's Microsoft 365 tenant) | outbound email via Graph | tenant data held in the UK (Philip, 27/10/2026) |
| Hostinger | DNS only; no personal data | — |

Data processing terms with each: each provider's standard data processing terms apply (DigitalOcean, Cloudflare, Microsoft; Philip, 27/10/2026). International transfers: none restricted (DigitalOcean London, R2 EU jurisdiction, Microsoft 365 UK).

## 3. Lawful basis (proposed, for Philip to confirm)

| Processing | Article 6 basis (proposed) | Article 9 / 10 condition (proposed) |
|---|---|---|
| Student and parent records, progress, certificates | legitimate interests in running the club (confirmed, Philip 27/10/2026) | n/a |
| Consent-dependent activities (photos, trips, data sharing) | consent, given by the parent annually | n/a |
| Medical, dietary/SEN, emergency contact | legitimate interests (confirmed) | Art 9(2)(d) (confirmed, Philip 27/10/2026; the annual parental consent form is the additional safeguard), not-for-profit body with a religious aim, processing about members or people in regular contact, not disclosed outside without consent ([ICO: conditions for processing](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/what-are-the-conditions-for-processing/)) |
| DBS check records | legitimate interests (confirmed) | DPA 2018 Schedule 1 paragraph 31, not-for-profit body with a religious aim, members and people in regular contact, not disclosed outside without consent (text checked on [legislation.gov.uk](https://www.legislation.gov.uk/ukpga/2018/12/schedule/1/part/3); no appropriate policy document required for it); fallback paragraph 18, safeguarding of children, which does need one, so one is kept anyway (`governance/appropriate-policy-document.md`) (Philip, 27/10/2026). Only the fact of a check, its certificate number and dates are recorded, never its content; the Safeguarding Officer checks the certificate and updates the record |
| Emails to parents and volunteers | legitimate interests | n/a |

## 4. Necessity and proportionality

- **Minimisation:** only the fields above; consent categories match the club's paper form; no photo storage; DBS certificate number and dates only.
- **Accuracy:** Admin/SO record consent and DBS; Teachers can reverse their own completion entries; parents see their child's record and can ask for corrections.
- **Retention:** students 3 years after leaving; DBS records and the access log 6 years; hard delete (`governance/security-compliance.md`). A "records due for deletion" report lets an Admin delete each record, confirmed by typing the person's name; a monthly email reminds each chapter's Admins while records are due; the access log purges itself after 6 years; each deletion is recorded in a permanent deletion log without personal data (`adr/0014`, built 27/10/2026). Backups hold deleted data until they expire (`operations/dr-bcp.md` §2, R2 retention `[TBC – Philip]`).
- **Transparency:** privacy notice (`governance/privacy-notice.md`, draft) given to parents at enrolment and linked from the app.
- **Rights:** access, rectification and erasure requests handled by Philip through a church email address (Philip, 27/10/2026). Not automated decision-making with legal or similarly significant effect: promotion and consent blocking apply fixed club rules to records.

## 5. Risks and measures

Likelihood and severity: Remote / Possible / Probable; Minimal / Significant / Severe.

| # | Risk | L | S | Measures in place | Residual |
|---|---|---|---|---|---|
| 1 | A volunteer without the role sees a child's medical data or a DBS record | Possible | Severe | Whole-entity query filters (Admin/SO only, MFA required); Approver curriculum-only; tested (TC-007, TC-008); ASVS V8 gate stage | Low |
| 2 | Authorised access is misused without trace | Possible | Significant | Every read of `DbsCheck`/`ConsentSensitiveDetails` logged (TC-011); log protected by PostgreSQL RLS (TC-014) | Low |
| 3 | A stranger claims to be a parent and sees a child's data | Possible | Severe | Guardian links only by Admin/Teacher; invite acceptance grants no role until Admin approves (FR-004, TC-012) | Low |
| 4 | Account takeover | Possible | Severe | ASP.NET Core Identity; MFA mandatory for Admin/SO and (since 22/10/2026) Teachers; lockout after 5 attempts; rate limiting; confirmed email; HSTS | Low–medium (MFA optional for Parents, Students and Approvers) |
| 5 | Server compromise or data breach | Remote | Severe | Hardened host (`operations/host-hardening.md`), least-privilege DB roles, secrets outside the repo, secret scanning, security updates | Medium (single operator) |
| 6 | Data loss | Possible | Significant | Nightly off-box backups, restore testing (`operations/dr-bcp.md`) | Low once the restore test is done |
| 7 | Data kept longer than the retention period | Possible | Significant | Due-for-deletion report with Delete per record, confirmed by typing the name (Admin, own chapter); monthly email to each chapter's Admins while records are due; read-audit log purged automatically after 6 years; permanent deletion log (`adr/0014`, built 27/10/2026) | Low: deletion stays manual, so a record can outlive its date if Admins ignore the reminder (accepted, `adr/0014`); backups keep deleted data until they expire |
| 8 | A safeguarding alert (expired consent/DBS) goes unnoticed | Possible | Significant | Retries; escalation logged; digest email to Admin/SO | **Low once deployed and tested on the server:** alert route built 27/10/2026 (`Vlms.Jobs alert-digest`, run daily by `vlms-alert.timer` and at once by `OnFailure=` on every `vlms-*` unit, emailing `alerts@varangians.co.uk`; `/health` for the uptime check; disk alert from the host monitoring agent; `operations/monitoring.md` §3, §5). Not yet exercised on a live host (`deploy/README.md` §5a) |
| 9 | Personal data in server logs | Probable | Minimal | Journal retention capped (`operations/monitoring.md` §2) | Low |
| 10 | Breach not reported in time | Possible | Significant | Incident steps in `operations/runbook.md` §5 | Low: Philip reports a reportable breach to the ICO within 72 hours, following `operations/runbook.md` §5 (Philip, 27/10/2026) |
| 11 | Applications, including rejected ones about children who never joined, are kept longer than needed | Possible | Significant | Kept 1 year after the decision (Philip, 23/10/2026), then flagged on the "records due for deletion" report; restricted store, read-audited; not emailed | Low: deleted by an Admin from the report, with a monthly reminder (risk 7) |
| 12 | Someone who is not the child's parent applies and enters a child's special category data | Possible | Significant | Two-stage staff review before anything is linked; applicant sees only their own applications; same child name under one applicant refused | Accepted (Philip, 27/10/2026): two-stage staff review before anything is linked |

## 6. Sign-off

| Item | Name / date | Notes |
|---|---|---|
| Measures approved by | Philip Thomas, Secretary and Safeguarding Trustee, 27/10/2026 | |
| Residual risks approved by | Philip Thomas, 27/10/2026 | Risks 7 and 8 accepted as Low once deployed (8 needs its live test on the server); risk 12 accepted |
| DPO / data-protection advice | None required (Philip, 27/10/2026) | Not a public authority; no large-scale special category or criminal offence data |
| ICO registration (data protection fee) | Not required (Philip, 23/10/2026) | Not-for-profit exemption; lost if, for example, the church becomes responsible for CCTV or shares personal data commercially ([ICO: fee exemptions](https://ico.org.uk/for-organisations/data-protection-fee/data-protection-fee/exemptions/)) |
| Review date | 27/10/2027 | Annually, or on any change to the data collected |
| Safeguarding Policy wording | To change (Philip, 27/10/2026) | The policy's line that safeguarding records are seen only by the Safeguarding Lead and Deputy becomes "the Safeguarding Lead, the Deputy and the club's administrators", matching the app's Admin access. Philip amends the church's policy document |
