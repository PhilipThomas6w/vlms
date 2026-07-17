# Glossary

Status: initial (design gate)

| Term | Meaning |
|---|---|
| VLMS | Varangians LMS — this project's name and document code. |
| Rank | A level in the programme's progression ladder (e.g. Recruit through to Akolouthos). |
| Lesson | A unit of curriculum content, belonging to a Rank. |
| LessonChangeProposal | A Teacher-submitted proposed create/edit/retire change to a Lesson, requiring Approver sign-off. |
| Approver | The role that approves/rejects curriculum (lesson) changes only — no safeguarding, consent, or DBS privileges. |
| Safeguarding Officer | The role (alongside Admin) with access to DBS checks and sensitive consent data. |
| StudentLessonCompletion | A record of a Student completing a Lesson, marked by a Teacher. |
| Certificate | An auto-generated PDF document issued per lesson completion. |
| RankBadge / StudentBadge | A badge associated with a Rank, awarded to a Student on promotion. |
| ConsentRecord | The annual parental consent record for a Student (status/expiry, non-sensitive fields). |
| ConsentSensitiveDetails | The medical/dietary-SEN/emergency-contact fields split out from `ConsentRecord`, restricted to Admin/Safeguarding Officer. |
| DBS | Disclosure and Barring Service (gov.uk) — the UK body whose checks are tracked against Teachers. |
| StudentGuardianLink | The link between a Student and a ParentGuardian, created only by Admin/Teacher, never by parent self-service. |
| SensitiveDataAccessLog | The audit trail of reads (not just writes) of `DbsCheck`/`ConsentSensitiveDetails`. |
| CIAM | Customer Identity and Access Management — the category of identity service Microsoft Entra External ID provides. |
| ACS | Azure Communication Services — used here for transactional email. |
| PWA | Progressive Web App — an installable, browser-based app; how VLMS's Blazor Web App reaches phone/tablet/browser from one codebase. |
| ADR | Architecture Decision Record (Nygard format) — see `adr/`. |
| BR / FR / NFR | Business Requirement / Functional Requirement / Non-Functional Requirement — traceability ID prefixes used throughout `docs/`. |
| RTM | Requirements Traceability Matrix (`quality/traceability.md`). |
| RAID | Risks, Assumptions, Issues, Dependencies log (`governance/raid.md`). |
| WCAG | Web Content Accessibility Guidelines (2.2 AA is this project's confirmed conformance target). |
