# Delivery Plan

Status: initial (design gate) — light-touch, proportionate to a solo-delivered project (ISO 21502:2020 stage-boundary guidance applied informally, not as full PRINCE2/PMBOK machinery).

## Phases

1. **Discovery & design (complete)** — requirements, ADR-0001–0004, data/low-level design; this design gate.
2. **Build (MVP), reopened 22/10/2026.** Walking the user journeys (`requirements/user-journeys.md`) showed that the app as built was not usable for its purpose. Lessons could not be opened, completions could not be recorded in the app, certificates could not be downloaded, students had no page, and the home page was still the template. The services and their tests were sound, but the pages that let users reach them were missing, and lesson content access had been wrongly deferred at discovery. Remaining build, in order (`STATE.md` holds the live queue):
   1. Lesson library: view and download (FR-006).
   2. Seed the rank ladder and curriculum.
   3. Home page and navigation per role (FR-012).
   4. Teacher's student list (FR-009).
   5. Record lesson completion (FR-008).
   6. Certificate download (FR-010).
   7. Lesson uploads and Approver review (FR-007).
   8. Edit student details (FR-013).
   9. Rank badge images (FR-014).
   10. Parent consent form (FR-015), after the ADR-0004 decision.
   11. Student accounts and student page (FR-011), after the student-account decision.
   Then: the purge, branding/contrast and go-live items already in `STATE.md`.
   Original phase 2 description: implementation via `loop-harness` `/work-next` cycles (one item at a time, in-session, gated by `build/verify.ps1`) against `STATE.md`, not a fixed Gantt schedule (solo delivery, no hard deadline — `requirements/constraints.md`). Scope: curriculum management (propose/approve/resubmit), progress tracking (completion, auto-promotion, certificates, badges), safeguarding/consent/DBS management including the corrected access-control model (`adr/0004`), parent dashboard + notifications, core reporting + at-risk flagging.
3. **Delivery gate** — full test results, RTM completion, security/DPIA sign-off (`raid.md` D-003), operations readiness (runbook, monitoring — to be scoped at that gate, not over-specified now).
4. **Deferred (post-MVP):** order and stages in `roadmap.md`, including the offline-capable .NET MAUI app (`adr/0011`). at-home learning encouragement beyond lesson content access (`requirements/business-requirements.md`). Lesson content access itself is MVP and a go-live blocker (Philip, 22/10/2026).

## Delivery approach

- Iterative, solo-developer delivery — no fixed milestone dates, no external delivery team to coordinate.
- Definition of done for build: the `loop-harness` verify gate exits 0, not a subjective "looks right" call (per the engineer's own loop-engineering practice).

## Exit criteria for the delivery gate (named now, not left implicit)

- Every journey in `requirements/user-journeys.md` walked end to end in the running app by each role, with no step marked Missing or Partial.
- The delivery gate package rendered on 13/10/2026 (4b52355) is superseded: it was rendered before these gaps were found and must be re-rendered.
- DPIA screening complete (`raid.md` D-003).
- Certificate template + delivery mechanism decided and built (`requirements/functional.md`).
- OWASP ASVS 5.0 access-control/logging conformance checked against the built system (`adr/0004-sensitive-data-access-control.md`).
- All `TC-nnn` in `quality/test-plan.md` passing, RTM (`quality/traceability.md`) complete.
