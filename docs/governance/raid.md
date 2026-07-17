# RAID Log

Status: in progress (per ISO 31000:2018 — proportionate to a solo-delivered project)

## Risks

- **R-001:** Special-category/children's data (safeguarding, medical, consent, DBS) is managed in-system; a data-protection failure has real safeguarding consequences, not just reputational cost. Mitigation approach: to be addressed in `governance/security-compliance.md` and via OWASP ASVS-aligned NFRs.
- **R-002:** Solo developer/maintainer — no delivery redundancy. Bus-factor risk for ongoing operation and support.

## Assumptions

- **A-001:** No external safeguarding governing body/regulator beyond general UK law (confirmed with owner).
- **A-002:** Scale remains "tens of users per role" over the ~2-year horizon; architecture is not being designed for hundreds/thousands of users.
- **A-003:** No data migration from the prior spreadsheets — clean start.

## Issues

- None currently open.

## Dependencies

- **D-001:** Certificate PDF generation requires a template design and a document-generation approach — not yet selected (see `requirements/functional.md`).
- **D-002:** Outbound transactional email capability (notifications, reminders, certificates) — provider not yet selected.
