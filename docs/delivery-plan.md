# Delivery Plan

Status: initial (design gate) — light-touch, proportionate to a solo-delivered project (ISO 21502:2020 stage-boundary guidance applied informally, not as full PRINCE2/PMBOK machinery).

## Phases

1. **Discovery & design (complete)** — requirements, ADR-0001–0004, data/low-level design; this design gate.
2. **Build (MVP)** — implementation via `loop-harness` maker/checker cycles against `STATE.md`, not a fixed Gantt schedule (solo delivery, no hard deadline — `requirements/constraints.md`). Scope: curriculum management (propose/approve/resubmit), progress tracking (completion, auto-promotion, certificates, badges), safeguarding/consent/DBS management including the corrected access-control model (`adr/0004`), parent dashboard + notifications, core reporting + at-risk flagging.
3. **Delivery gate** — full test results, RTM completion, security/DPIA sign-off (`raid.md` D-003), operations readiness (runbook, monitoring — to be scoped at that gate, not over-specified now).
4. **Deferred (post-MVP):** at-home learning encouragement (`requirements/business-requirements.md`).

## Delivery approach

- Iterative, solo-developer delivery — no fixed milestone dates, no external delivery team to coordinate.
- Definition of done for build: the `loop-harness` verify gate exits 0, not a subjective "looks right" call (per the engineer's own loop-engineering practice).

## Exit criteria for the delivery gate (named now, not left implicit)

- DPIA screening complete (`raid.md` D-003).
- Certificate template + delivery mechanism decided and built (`requirements/functional.md`).
- OWASP ASVS 5.0 access-control/logging conformance checked against the built system (`adr/0004-sensitive-data-access-control.md`).
- All `TC-nnn` in `quality/test-plan.md` passing, RTM (`quality/traceability.md`) complete.
