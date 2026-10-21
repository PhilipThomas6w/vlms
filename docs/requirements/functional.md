# Functional Requirements

Status: in progress

## Curriculum management (MVP)

- Any Teacher can propose a change to a lesson (content edit, new lesson, retire a lesson).
- Only the Approver role can sign off / approve a proposed change before it goes live.
- Review is ad hoc only — no scheduled/termly review prompt; teachers propose changes whenever they spot something worth changing.
- Rejection supports comments and resubmission: the Approver can leave feedback on a rejected proposal, and the Teacher can revise and resubmit the same proposal (not just raise a fresh one).
- Each lesson has a delivery order within its rank and one or more topic tags from a fixed list taken from the programme syllabus: Church Life, Worship, Church Interior, Mysteries, Prayers, Saints, Church History, Old Testament, New Testament, Church Year, Christian Life (Philip's decision, 21/10/2026).
- A lesson can have several content files (a main document plus images and handouts), not a single file (Philip's decision, 21/10/2026).
- The initial curriculum is seeded from `seed/curriculum.json`. Ranks 1–7 have 8 lessons each; Cross-bearer (8), Christ-bearer (9) and Akolouthos (10) are seeded with no lessons, to be filled later. The lesson files are supplied at seed time and are not committed to the repository (Philip's decision, 21/10/2026).
- **Lesson content access (MVP, go-live blocker; Philip, 22/10/2026):** the core purpose of the app. Lesson files are opened in the app or downloaded by:
  - **Teachers:** all lessons in every rank.
  - **Students:** all lessons in every rank, not only their own.
  - **Parents:** all lessons in every rank.
  - **Uploads:** a teacher uploads new or replacement files as part of a lesson change proposal, and they go live only when the Approver approves it, never directly.
  Discovery (a300c93, 17/07/2026) recorded student access to lesson material as deferred "at-home learning", and the user journeys were never written, so content access was never built. Philip confirms it was the key requirement, and this entry corrects the record.
- **Longer-term aim (post-MVP):** each lesson's content becomes a single printable HTML page, with handouts attached, that users read in the app without downloading anything. This replaces the current Word documents (Philip, 21/10/2026).

## Progress tracking (MVP)

- Auto-promotion: a student is automatically promoted to the next rank once every (active) lesson in their current rank is marked complete.
- Certificates (per lesson completion) and badges (per rank promotion) are real tracked records in the system — not implicit/ad hoc — giving an audit trail of who received what and when.
- The Teacher marks a lesson complete for a student, and can self-correct/reverse their own entry (e.g. within some reasonable window) — no separate correction gate through Admin. An evidence/note field is available but optional (low friction, not a mandatory audit gate).
- Certificates are auto-generated documents (PDF, from a template) at completion time — not just a database record. This resolves the ambiguity in the prior build (which left certificate generation unspecified). Implies a template + document-generation component in design, and storage/delivery (download vs email) — [TBC design].

## Safeguarding & consent (MVP) — confirmed matching prior build's model

Discrete `FR-nnn` numbering assigned here specifically (per design review), given the safeguarding weight of this module — other sections remain narrative pending fuller FR-nnn numbering before build.

- **FR-001:** Annual parental consent record per student covering: photo/media consent, emergency medical info, dietary/SEN needs, transport/off-site trip consent, data-sharing consent. Approved by Safeguarding Officer or Admin only.
- **FR-002:** DBS (Disclosure and Barring Service) check tracking for teachers. Whole-record access restricted to Admin and Safeguarding Officer only — Teacher and Approver have no access (`governance/security-compliance.md`).
- **FR-003:** Expiry monitoring: consent and DBS expiry are tracked, with escalating alerts, and **expired consent blocks lesson completion** for that student until renewed.
- **FR-004:** A `StudentGuardianLink` (parent-to-child relationship) is created only by Admin/Teacher at student registration — never by parent self-service — before a parent can access any progress, medical, or consent data for that child.
  - Accepting a guardian invite creates the account only; it grants no role. An Admin approves the guardian by granting `Parent` (Philip's decision, 11/10/2026).
- **FR-005:** Every read of a `DbsCheck` record, or of a `ConsentSensitiveDetails` record, is written to an audit log (who, what, when).

## Parent engagement (MVP)

- Parents get both an in-app dashboard (view own child's progress, certificates, badges) AND proactive notifications (e.g. email on lesson completion/rank promotion, consent-expiry reminders) — not dashboard-only.

## Reporting (MVP)

- Core progress reports: rank/completion stats, promotion history.
- At-risk/disengaged student flagging: no lesson completions within 8 weeks, surfaced to Admin for proactive follow-up.
