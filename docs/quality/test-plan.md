# Test Plan

Status: strategy (design gate) — per ISO/IEC/IEEE 29119-3:2021. Completed with results at the delivery gate.

## Approach & levels

- **Unit tests** (`Vlms.Tests`) — domain logic in `Vlms.Domain`: promotion rules, proposal state machine (propose/approve/reject/resubmit), consent-expiry blocking logic, at-risk threshold calculation.
- **Integration tests** — EF Core against a real (local/test) Azure SQL instance or SQL container: entity relationships, column-masking/access-restriction enforcement (`DbsCheck`, `ConsentRecord` sensitive fields) per role, certificate generation round-trip (QuestPDF → Blob Storage).
- **End-to-end tests** — key Blazor user journeys per role (Teacher marks completion → promotion → certificate; Teacher proposes change → Approver approves/rejects → resubmission; Parent views own child only, not others; consent expiry blocks completion).
- **Accessibility testing** — WCAG 2.2 AA conformance check against core screens (automated tooling + manual spot-check), per `requirements/non-functional.md`.
- **Security testing** — OWASP ASVS 5.0-aligned checklist review at delivery gate, focused on access control (role/resource-based authorization) given the safeguarding data in scope.

## Environments

- Local development (solo developer machine).
- A single Azure staging slot (if the App Service tier supports it — [TBC: confirm once Standard tier is provisioned per `adr/0001-technology-stack.md` consequences]) before production.

## Entry/exit criteria (initial)

- Entry: feature complete against its FR/NFR, unit tests passing locally.
- Exit (delivery gate): all TC-nnn below passing, no open Critical/High severity defects, WCAG 2.2 AA spot-check clean, access-control tests for DBS/consent masking passing.

## Initial test case skeleton (TC-nnn)

| TC | Scenario | Traces to |
|---|---|---|
| TC-001 | Teacher marks lesson complete; consent valid → completion recorded, certificate generated | FR (progress tracking), FR (certificates) |
| TC-002 | Teacher marks lesson complete; consent expired → blocked | FR (safeguarding & consent) |
| TC-003 | Student completes all active lessons in current rank → auto-promoted, badge awarded | FR (progress tracking) |
| TC-004 | Teacher proposes lesson change → Approver approves → live | FR (curriculum management) |
| TC-005 | Approver rejects with comments → Teacher resubmits → Approver approves | FR (curriculum management) |
| TC-006 | Parent views own child's dashboard; cannot view another parent's child | NFR (security/access control) |
| TC-007 | Teacher attempts to view a `DbsCheck` record → denied | NFR (security/access control), governance/security-compliance.md |
| TC-008 | Approver attempts to view/approve a `ConsentRecord` → denied (out of role remit) | NFR (security/access control) |
| TC-009 | Consent/DBS expiry sweep flags and notifies correctly at threshold | FR (safeguarding & consent) |
| TC-010 | At-risk flag raised after 8 weeks with no completion | FR (reporting) |

Full RTM: `quality/traceability.md`.
