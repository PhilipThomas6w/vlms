# Business Requirements

Status: in progress

## Scope & phasing (MoSCoW, high level — to be broken into BR-nnn once design starts)

**Must have (MVP):**
- Progress tracking: student lesson completions, rank progression (auto-promotion logic carried over as a hypothesis from prior build — [TBC confirm]).
- Curriculum management process: periodic lesson review, propose changes, approve, incremental improvement over time — with distinct Teacher (propose) and Approver (approve) roles.
- Usable by every role in the app: teachers record completions, students and parents see progress and download certificates, and parents submit the annual consent form (`functional.md` FR-008 to FR-015; Philip, 22/10/2026).
- Lesson content access: teachers, students and parents open and download lesson content in the app, and teachers upload it through Approver-approved proposals (`functional.md`). This is the app's core purpose and was wrongly recorded as deferred "at-home learning" at discovery (Philip, 22/10/2026).

**Deferred (not MVP):**
- At-home learning encouragement beyond lesson content access, such as assignments set for between sessions. Access to the lesson material itself is now Must have (above).

**Confirmed MVP addition:** safeguarding/consent/DBS tracking (medical info, photo/media consent, DBS checks) is managed *inside* this system, not left to an external process. This is a hard requirement, not a nice-to-have — it carries real legal/safeguarding weight (see `governance/security-compliance.md` and `requirements/non-functional.md`).
