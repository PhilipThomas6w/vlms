# RAID Log

Status: in progress (per ISO 31000:2018 — proportionate to a solo-delivered project)

## Risks

- **R-001:** Special-category/children's data (safeguarding, medical, consent, DBS) is managed in-system; a data-protection failure has real safeguarding consequences, not just reputational cost. Mitigation: `governance/security-compliance.md`, OWASP ASVS-aligned NFRs, and — added across two design-review passes — the `ConsentRecord`/`ConsentSensitiveDetails` entity split with structural whole-entity access control (`adr/0004-sensitive-data-access-control.md`: EF Core global query filters + `DbCommandInterceptor`-based read audit log) and verified guardian-linking (`design/data-design.md`), closing gaps the initial design left open (no read-audit trail; no verification that a self-registering parent was genuinely a child's guardian; a masking mechanism that couldn't actually mask individual columns).
- **R-002:** Solo developer/maintainer — no delivery redundancy. Bus-factor risk for ongoing operation and support.

## Assumptions

- **A-001:** No external safeguarding governing body/regulator beyond general UK law (confirmed with owner).
- **A-002:** Scale remains "tens of users per role" over the ~2-year horizon; architecture is not being designed for hundreds/thousands of users.
- **A-003:** No data migration from the prior spreadsheets — clean start.

## Issues

- None currently open.

## Dependencies

- **D-001:** Certificate PDF generation approach selected (QuestPDF, `adr/0001-technology-stack.md`); a certificate template still needs to be designed before build (see `requirements/functional.md`).
- **D-002:** Outbound transactional email provider selected (Azure Communication Services Email, `adr/0001-technology-stack.md`); a verified sending domain is still required before go-live (see `adr/0001-technology-stack.md` consequences).
- **D-003 (added at second design review):** DPIA (Data Protection Impact Assessment) screening against ICO guidance is required given special-category and children's data are processed (UK GDPR Art. 35). **Owner: Philip. Due: before go-live**, not deferred to an undated future document — this is a dated, owned commitment recorded here directly rather than a forward reference to `delivery-plan.md`.
