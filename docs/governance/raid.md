# RAID Log

Status: in progress (per ISO 31000:2018 — proportionate to a solo-delivered project)

## Risks

- **R-001:** Special-category/children's data (safeguarding, medical, consent, DBS) is managed in-system; a data-protection failure has real safeguarding consequences, not just reputational cost. Mitigation: `governance/security-compliance.md`, OWASP ASVS-aligned NFRs, and — added across three design-review passes — the `ConsentRecord`/`ConsentSensitiveDetails` entity split with structural whole-entity access control (`adr/0004-sensitive-data-access-control.md`: EF Core global query filters + `IMaterializationInterceptor`-based read audit log) and verified guardian-linking (`design/data-design.md`), closing gaps the initial design left open (no read-audit trail; no verification that a self-registering parent was genuinely a child's guardian; a masking mechanism that couldn't actually mask individual columns; an audit-write mechanism that couldn't reliably identify which entity was read).
- **R-002:** Solo developer/maintainer — no delivery redundancy. Bus-factor risk for ongoing operation and support.

## Assumptions

- **A-001:** No external safeguarding governing body/regulator beyond general UK law (confirmed with owner).
- **A-002:** Scale remains "tens of users per role" over the ~2-year horizon; architecture is not being designed for hundreds/thousands of users.
- **A-003:** No data migration from the prior spreadsheets — clean start.

## Issues

- None currently open.

## Dependencies

- **D-001:** Certificate PDF generation approach selected (QuestPDF, `adr/0001-technology-stack.md`); a certificate template still needs to be designed before build (see `requirements/functional.md`).
- **D-002 (updated by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`):** Outbound transactional email now goes via SMTP (the organisation's existing Microsoft 365 mailbox, or a free-tier transactional provider — final pick tracked as a build item) rather than Azure Communication Services Email. A verified sending domain (SPF/DKIM/DMARC) is provider-agnostic and is still required before go-live regardless of which SMTP path is chosen.
- **D-003 (added at second design review):** DPIA (Data Protection Impact Assessment) screening against ICO guidance is required given special-category and children's data are processed (UK GDPR Art. 35). **Owner: Philip. Due: before go-live**, not deferred to an undated future document — this is a dated, owned commitment recorded here directly rather than a forward reference to `delivery-plan.md`.
- **D-004 (added when the ADR-0004 §4 tamper-protection DENY was re-targeted to `public`; generalised by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`'s move to PostgreSQL):** the production application must connect to the database as a **least-privilege role** — able to `SELECT`/`INSERT` on `sensitive_data_access_log` but never able to `UPDATE`/`DELETE` it, and never the table owner or a superuser/`postgres` role. PostgreSQL's `REVOKE UPDATE, DELETE ON sensitive_data_access_log FROM PUBLIC` (replacing SQL Server's `DENY ... TO public`) is bypassed only by the table owner and superuser roles, so running the app as either would silently defeat the control — the same residual check that survived the original SQL Server design, ported rather than redesigned. Relatedly, any future retention-purge job for `SensitiveDataAccessLog` (the separate 6-year retention concern) must run as a deliberately elevated role, since the app's role cannot delete audit rows. **Owner: Philip. Due: before go-live** (a deployment/operations-readiness check, per `delivery-plan.md` Phase 3). See `adr/0004-sensitive-data-access-control.md` §4, `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`.
