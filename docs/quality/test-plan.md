# Test Plan

Status: strategy (design gate) — per ISO/IEC/IEEE 29119-3:2021. Completed with results at the delivery gate.

## Approach & levels

- **Unit tests** (`Vlms.Tests`) — domain logic in `Vlms.Domain`: promotion rules, proposal state machine (propose/approve/reject/resubmit), consent-expiry blocking logic, at-risk threshold calculation.
- **Integration tests** — EF Core against a real (local/test) Azure SQL instance or SQL container: entity relationships, whole-entity access-restriction enforcement (`DbsCheck`, `ConsentSensitiveDetails`) per role, `ConsentRecord` (status/expiry) remaining readable to Teacher/Approver despite the restriction on its split-out sensitive sibling, certificate generation round-trip (QuestPDF → Blob Storage).
- **End-to-end tests** — key Blazor user journeys per role (Teacher marks completion → promotion → certificate; Teacher proposes change → Approver approves/rejects → resubmission; Parent views own child only, not others; consent expiry blocks completion).
- **Accessibility testing** — WCAG 2.2 AA conformance check against core screens (automated tooling + manual spot-check), per `requirements/non-functional.md`.
- **Security testing** — OWASP ASVS 5.0-aligned checklist review at delivery gate, focused on access control (role/resource-based authorization) given the safeguarding data in scope.

## Environments

- Local development (solo developer machine).
- **No Azure staging slot** — confirmed at design review: deployment slots require App Service Standard tier or above ([Deploy staging slots](https://learn.microsoft.com/azure/app-service/deploy-staging-slots)), and ADR-0001 selects Basic (B1), which has none. Testing goes local/dev → production directly; this is an accepted trade-off for cost at tens-of-users scale, not an oversight. Revisit if `adr/0001-technology-stack.md` is ever superseded by an upgrade to Standard tier.

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
| TC-007 | Teacher attempts to view a `DbsCheck` record → denied; Teacher can still read `ConsentRecord.Status`/`ExpiryDate` for the same student | NFR (security/access control), `adr/0004-sensitive-data-access-control.md` |
| TC-008 | Approver attempts to view/approve a `ConsentRecord` or `ConsentSensitiveDetails` → denied (out of role remit) | NFR (security/access control) |
| TC-009 | Consent/DBS expiry sweep flags and notifies correctly at threshold | FR (safeguarding & consent) |
| TC-010 | At-risk flag raised after 8 weeks with no completion | FR (reporting) |
| TC-011 | Any read of a `DbsCheck` or `ConsentSensitiveDetails` row writes a `SensitiveDataAccessLog` entry via the `DbCommandInterceptor` | `adr/0004-sensitive-data-access-control.md` |
| TC-012 | A `StudentGuardianLink` cannot be created by parent self-service — only by Admin/Teacher at registration | `data-design.md` (Guardian link verification) |
| TC-013 | A failed safeguarding-critical notification (expired consent/DBS) is retried, then escalated as a visible failure to Admin rather than silently dropped | `low-level-design.md` (NotificationService failure handling) |
| TC-014 | Attempting `UPDATE`/`DELETE` against `SensitiveDataAccessLog` as the application's database principal is denied | `adr/0004-sensitive-data-access-control.md` (tamper protection) |

Full RTM: `quality/traceability.md`.
