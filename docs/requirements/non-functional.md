# Non-Functional Requirements

Status: in progress (ISO/IEC 25010:2023 checklist)

## Availability/reliability

- Best-effort — no formal uptime SLA/RPO/RTO target. Brief downtime for deploys/maintenance is acceptable. Must reliably work during actual usage windows (evenings/weekends around sessions).

## Interaction capability / accessibility

- WCAG 2.2 AA conformance — a firm, testable NFR (not just good-practice aspiration), given parents and students of varying ability use the system.

## Security

- See `governance/security-compliance.md` for the safeguarding/consent data classification drivers (UK GDPR/DPA 2018, special-category and children's data).
- Column-level access control/masking on sensitive fields, and audit logging of access — carried forward as a hypothesis from the prior build, to be confirmed as NFR-nnn during design.

## Performance, scalability, maintainability, compatibility

Status: [TBC] — scale is small (tens of users per role, see `stakeholders.md`), so demanding performance/throughput targets are unlikely to be the binding constraint, but not yet explicitly walked.
