# Functional Requirements

Status: in progress

## Curriculum management (MVP)

- Any Teacher can propose a change to a lesson (content edit, new lesson, retire a lesson).
- Only the Approver role can sign off / approve a proposed change before it goes live.
- Review is ad hoc only — no scheduled/termly review prompt; teachers propose changes whenever they spot something worth changing.
- Rejection supports comments and resubmission: the Approver can leave feedback on a rejected proposal, and the Teacher can revise and resubmit the same proposal (not just raise a fresh one).

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
- **FR-005:** Every read of a `DbsCheck` record, or of a `ConsentRecord`'s sensitive fields, is written to an audit log (who, what, when).

## Parent engagement (MVP)

- Parents get both an in-app dashboard (view own child's progress, certificates, badges) AND proactive notifications (e.g. email on lesson completion/rank promotion, consent-expiry reminders) — not dashboard-only.

## Reporting (MVP)

- Core progress reports: rank/completion stats, promotion history.
- At-risk/disengaged student flagging: no lesson completions within 8 weeks, surfaced to Admin for proactive follow-up.
