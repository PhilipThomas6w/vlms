# Appropriate policy document: special category and criminal offence data

Status: **adopted by Philip Thomas (Secretary and Safeguarding Trustee), 27/10/2026**. Review: annually with the DPIA (`governance/dpia.md`), next 27/10/2027.

This document is required by the Data Protection Act 2018 Schedule 1 Part 4 where a Schedule 1 condition needs one. It covers the Varangians app run by Shrewsbury Orthodox Church (a CIO, charity no. 1166735), the controller. Its content follows the ICO's guidance: the conditions relied on, how each principle is met, and retention and deletion ([ICO: appropriate policy document](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/special-category-data/what-are-the-rules-on-special-category-data/)).

## 1. Data and conditions

| Data | Condition |
|---|---|
| Children's medical, dietary/SEN and emergency details; church membership (may reveal religious belief) | UK GDPR Art 9(2)(d), not-for-profit body with a religious aim |
| DBS checks of volunteers: only that a check was done, its certificate number and dates; never its content | DPA 2018 Schedule 1 paragraph 31 (not-for-profit bodies), which needs no policy document; paragraph 18 (safeguarding of children) as fallback, which does, and this document meets it |

Both conditions apply only to members, former members and people in regular contact with the church in connection with its purposes, and the data is not disclosed outside the church without consent, except where the law requires it or to protect a child.

## 2. How the principles are met

- **Lawful, fair and transparent:** the privacy notice (`governance/privacy-notice.md`) is given with the consent form and linked in the app. Parents renew consent every year.
- **Purpose limitation:** used only to keep children safe at sessions and to keep volunteers' safeguarding checks current; no marketing, profiling or sharing.
- **Data minimisation:** no DBS certificate content or images, no photos, no religion field. The Safeguarding Officer checks each certificate and records only its existence, number and dates.
- **Accuracy:** parents update their child's details; the consent form is renewed annually; the Safeguarding Officer updates DBS records.
- **Storage limitation:** see section 3.
- **Integrity and confidentiality:** DBS records are seen only by Admins and Safeguarding Officers; consent details also by Teachers (read) and the child's linked parents. Every read is audit-logged. Two-factor sign-in for all staff; encrypted connections; hardened server; encrypted off-box backups (`docs/adr/0004`, `0006`; `operations/host-hardening.md`).
- **Accountability:** the DPIA, this document and the access-control checklist are kept up to date; Philip is responsible on the trustees' behalf.

## 3. Retention and deletion

Students' records 3 years after they leave; DBS records 6 years after expiry or the volunteer's leaving; the read-audit log 6 years. Deletion is permanent, confirmed by an Admin from the "records due for deletion" report, with a monthly reminder; the audit log purges itself (`docs/adr/0014`). Deleted data remains in backups until they expire.
