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
  Detailed as FR-006 (library, view and download) and FR-007 (uploads and Approver review) below.
  Discovery (a300c93, 17/07/2026) recorded student access to lesson material as deferred "at-home learning", and the user journeys were never written, so content access was never built. Philip confirms it was the key requirement, and this entry corrects the record.
- **Longer-term aim (post-MVP):** each lesson's content becomes a single printable HTML page, with handouts attached, that users read in the app without downloading anything. This replaces the current Word documents (Philip, 21/10/2026).

## Progress tracking (MVP)

- Auto-promotion: a student is automatically promoted to the next rank once every (active) lesson in their current rank is marked complete.
- Certificates (per lesson completion) and badges (per rank promotion) are real tracked records in the system — not implicit/ad hoc — giving an audit trail of who received what and when.
- The Teacher marks a lesson complete for a student, and can self-correct/reverse their own entry (e.g. within some reasonable window) — no separate correction gate through Admin. An evidence/note field is available but optional (low friction, not a mandatory audit gate).
- Certificates are auto-generated documents (PDF, from a template) at completion time — not just a database record. This resolves the ambiguity in the prior build (which left certificate generation unspecified). Implies a template + document-generation component in design, and storage/delivery (download vs email) — [TBC design].

## Safeguarding & consent (MVP) — confirmed matching prior build's model

Discrete `FR-nnn` numbering assigned here specifically (per design review), given the safeguarding weight of this module — other sections remain narrative pending fuller FR-nnn numbering before build.

- **FR-001:** Annual parental consent record per student covering: photo/media consent, emergency medical info, dietary/SEN needs, transport/off-site trip consent, data-sharing consent. Approved by Safeguarding Officer or Admin only. Parents complete and submit it themselves in the app (FR-015).
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

## Fitness-for-purpose requirements (MVP, go-live blockers; Philip, 22/10/2026)

These come from walking the user journeys (`user-journeys.md`) against the built app on 22/10/2026. The MVP had been built and gate-tested at service level, but a user in the named role could not do these things in the app. A requirement here is met only when a user in each named role can complete it in the running app.

- **FR-006 Lesson library.** Teachers, Approvers, Admins, students and parents can browse every lesson in every rank, in delivery order, with tags, and open or download each lesson file. Files open in the browser where it can display them (PDF, images); other types, such as `.docx`, download. Files are served only to signed-in users holding one of those roles, never through a public link.
- **FR-007 Lesson uploads and Approver review.** A teacher uploads new or replacement files as part of a lesson proposal, with file size and type limits. Pending files are visible only to the proposer and Approvers, and go live only when the Approver approves. The Approver sees the proposal in readable form next to the current lesson and can open both sets of files.
- **FR-008 Record lesson completion.** A teacher picks a lesson, sees the students, and marks those who completed it in one action. A student blocked by lapsed consent (FR-003) is shown with the reason, not silently skipped. Certificate, promotion and parent email follow as already built.
- **FR-009 Teacher's student list.** Any teacher sees all active students with their current rank and progress within it, with a filter for students assigned to them, and can open a student's record (completions, badges, certificates) (Philip, 22/10/2026).
- **FR-010 Certificate view and download.** Students, their linked parents, teachers and Admins can view a student's certificate PDFs in the app and download them. This settles the delivery mechanism left [TBC] above. The certificate designs will come later (Philip, 22/10/2026); until then the existing generated PDF is used (`governance/raid.md` D-001).
- **FR-011 Student accounts and student page.** A parent creates a sign-in for each linked child: a username and password, with no email address needed (Philip, 22/10/2026). The parent can reset that password. A student has a page showing their rank, completions, badges and certificates, with a link to the lesson library.
- **FR-012 Home page and navigation.** Each role lands on a home page showing its next actions and has persistent navigation. This replaces the template home page.
- **FR-013 Student details.** A student's name, date of birth and other personal details come from the parent's consent form (FR-015): an approved consent submission updates the student record. Only Admins assign a student's teacher and change a student's status (Philip, 22/10/2026).
- **FR-014 Rank badge images.** An Admin uploads one badge image per rank, shown wherever badges are listed.
- **FR-015 Parent consent form.** A parent completes the annual consent form (all FR-001 fields) for each linked child in the app and submits it. A Safeguarding Officer or Admin approves it or rejects it with comments, and the parent sees the status and can resubmit. Access to `ConsentSensitiveDetails` (Philip, 22/10/2026): linked parents can view and update their own child's details; teachers can view them; Admin and Safeguarding Officer as before. This amends ADR-0004 (to be recorded as a new ADR). Every read stays audit-logged. `DbsCheck` stays Admin and Safeguarding Officer only. The DPIA must be updated, because parents enter, and teachers read, special-category data.
