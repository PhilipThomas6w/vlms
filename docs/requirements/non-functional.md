# Non-Functional Requirements

Status: in progress (ISO/IEC 25010:2023 checklist)

## Availability/reliability

- Best-effort — no formal uptime SLA/RPO/RTO target. Brief downtime for deploys/maintenance is acceptable. Must reliably work during actual usage windows (evenings/weekends around sessions).

## Interaction capability / accessibility

- WCAG 2.2 AA conformance — a firm, testable NFR (not just good-practice aspiration), given parents and students of varying ability use the system.

## Security (NFR-001)

- See `governance/security-compliance.md` for the safeguarding/consent data classification drivers (UK GDPR/DPA 2018, special-category and children's data).
- Column-level access control/masking on sensitive fields, and audit logging of **reads** (not just writes) of `DbsCheck`/`ConsentRecord` sensitive data — confirmed as a firm NFR, enforced via `adr/0004-sensitive-data-access-control.md`.

## Compatibility (NFR-004)

- Concrete conformance target, confirmed at design review (was previously unwalked): current versions of Chrome, Edge, Safari, and Firefox, on Windows, macOS, iOS, and Android, per [Blazor supported platforms](https://learn.microsoft.com/aspnet/core/blazor/supported-platforms) — matches the phone/tablet/browser access requirement in `requirements/constraints.md` without committing to legacy-browser support no user of this system needs.

## Performance, scalability

Status: carried forward, not yet walked in detail — scale is small (tens of users per role, see `stakeholders.md`), so demanding performance/throughput targets are unlikely to be the binding constraint. Acceptable to firm up at the delivery gate rather than block design approval.

## Other ISO/IEC 25010:2023 characteristics (addressed, not silently omitted)

- **Functional suitability:** covered by `requirements/functional.md` (FR-001–005 and narrative requirements) and `quality/traceability.md`, not repeated here.
- **Maintainability:** solo-maintainer constraint is the dominant driver of several architecture decisions already (ADR-0001 stack choice, ADR-0003 WebJobs over Functions, ADR-0004's structural-not-per-call enforcement) — treated as a cross-cutting design principle rather than a separate testable NFR.
- **Flexibility (scalability beyond the stated horizon):** explicitly not a design goal — `raid.md` A-002 states the ~2-year, tens-of-users horizon is what the architecture targets; scaling further is future rework, not a current requirement.
- **Safety:** no physical/operational safety characteristics apply to this system beyond the safeguarding *data* handling already covered under Security — noted here as genuinely not applicable, not overlooked.
