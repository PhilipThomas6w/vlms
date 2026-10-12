# VISION (standing spec, reread every session)

## Mission (five sentences)
VLMS is a fresh .NET rebuild of the Varangians youth programme's rank-progression and curriculum-management platform, replacing out-of-date spreadsheets and informal tracking. It tracks each student's lesson completions and auto-promotes them through a fixed rank ladder, issuing certificates (auto-generated PDFs) and badges as real tracked records. It runs a curriculum-management workflow where any Teacher can propose a lesson change and only the Approver (a role scoped solely to curriculum, with zero safeguarding privileges) can approve, reject-with-comments, or accept a resubmission. It manages safeguarding-critical data directly in-system — annual parental consent and DBS checks, with expiry monitoring that blocks lesson completion when consent lapses — under a strict, twice-corrected access-control model (whole-entity restriction, not column masking, plus a structural read-audit trail). It keeps parents informed via a dashboard and proactive notifications, and gives the Admin core progress and at-risk reporting, built and operated solo on Blazor + a self-hosted Linux VPS for a small (tens-of-users) audience with no hard deadline, at minimal ongoing hosting cost (`docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`).

## Hard constraints
- Stack: Blazor Web App (Server interactivity) + self-hosted Linux VPS (Kestrel behind Caddy) + self-hosted PostgreSQL + self-rolled ASP.NET Core Identity (TOTP MFA) + local disk file storage (off-box backup to a free-tier S3-compatible store) + SMTP email + QuestPDF (`docs/adr/0001-technology-stack.md` as amended by `docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`).
- Solo developer/maintainer — architecture favours fewest moving parts (a plain console app on a systemd timer rather than a scheduler SDK, `docs/adr/0003` as amended by `docs/adr/0005`; roles as application claims, not an external identity provider's groups, `docs/adr/0002`).
- Minimising ongoing hosting/storage cost is itself a hard constraint, not a soft preference — this system is funded by Shrewsbury Orthodox Church, which runs the Varangians youth club (`docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`, `docs/design/hosting-cost-review-2026-07-20.md`).
- Safeguarding data is special-category and children's personal data (UK GDPR/DPA 2018 applies directly):
  - `DbsCheck` and `ConsentSensitiveDetails` (medical/dietary-SEN/emergency-contact) are whole-entity restricted to Admin and Safeguarding Officer only, via EF Core global query filters — **not** column-level masking, which is technically impossible with that mechanism (`docs/adr/0004-sensitive-data-access-control.md`).
  - The **Approver** role is curriculum-approval only — never conflate it with consent/safeguarding sign-off.
  - Every read of `DbsCheck`/`ConsentSensitiveDetails` must write a `SensitiveDataAccessLog` entry via an `IMaterializationInterceptor`, tamper-protected at the database permission level (`REVOKE UPDATE/DELETE` on PostgreSQL, per `docs/adr/0005`) and retained 6 years.
  - `StudentGuardianLink` is created only by Admin/Teacher at registration, never by parent self-service.
  - Retention: student records 3 years from leaving/graduating; DBS records and the audit log 6 years; hard delete, not anonymisation.
- WCAG 2.2 AA is a firm, testable NFR, not an aspiration.
- No data migration — clean start.
- Scale target: tens of users per role over ~2 years — do not over-engineer for larger scale (`docs/governance/raid.md` A-002).
- DPIA screening against ICO guidance is owed before go-live (`docs/governance/raid.md` D-003).

## Current increment: acceptance criteria
MVP build, per `docs/delivery-plan.md` and `docs/requirements/functional.md`:
- Curriculum management: propose / approve / reject-with-comments / resubmit workflow live and tested (TC-004, TC-005).
- Progress tracking: lesson completion marking with Teacher self-correction, auto-promotion on rank completion, auto-generated PDF certificates, tracked badges (TC-001, TC-003).
- Safeguarding & consent: `ConsentRecord`/`ConsentSensitiveDetails` split correctly enforced, DBS tracking, expiry blocks completion, guardian-link verification, read-audit logging with tamper protection (TC-002, TC-007, TC-008, TC-009, TC-011, TC-012, TC-014).
- Parent engagement: dashboard + proactive notifications, with retry/escalation on failed safeguarding-critical sends (TC-013).
- Reporting: core progress reports + at-risk flagging at 8 weeks (TC-010).
- Access control: role- and resource-scoped authorization throughout, verified against OWASP ASVS 5.0 at build/test time (TC-006 plus the above).

## Where depth lives
- Full design and decisions: `docs/` (requirements/, design/, quality/, governance/, adr/) and the approved design gate package in `gates/design/`.
