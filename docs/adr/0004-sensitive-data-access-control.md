# 0004 - Sensitive data access control: entity split + EF Core query filters + read audit log

## Status

Accepted (revised after design review caught a flaw in the first version of this decision — see Context)

## Context

VLMS stores special-category and children's personal data directly (`DbsCheck`, and originally three sensitive fields on `ConsentRecord`) with a confirmed, role-specific access model: `DbsCheck` is restricted entirely to Admin/Safeguarding Officer; the sensitive consent fields are masked from Teacher and Approver, who nonetheless still need `ConsentRecord.Status`/`ExpiryDate` to enforce the consent-blocks-lesson-completion rule (FR-003).

A design review raised three issues, the first of which invalidated the original mechanism:

1. **The original decision specified EF Core global query filters to mask individual `ConsentRecord` columns.** This is not possible: a global query filter is a row-inclusion predicate (`HasQueryFilter`) — it can only include or exclude an entire row, not redact specific columns within a row that a role otherwise legitimately needs (Teacher needs `Status`/`ExpiryDate` from the same row the sensitive fields lived on). The mechanism as originally specified could not deliver the masking it claimed to.
2. If masking/restriction is instead implemented as an opt-in per-call convention, one missed query/report call site leaks sensitive data.
3. The original design logged who *created/approved* a safeguarding record but nothing logged who subsequently *read* one.

## Decision

1. **Split `ConsentRecord` into two entities** (`design/data-design.md`): `ConsentRecord` (status, expiry, non-sensitive consent flags — readable by Teacher/Approver) and `ConsentSensitiveDetails` (medical, dietary/SEN, emergency contact — 1:1 with `ConsentRecord`). This turns the masking requirement into the same *whole-entity* restriction pattern already correctly used for `DbsCheck`, which a query filter can actually enforce.
2. Enforce whole-entity restriction on `DbsCheck` and `ConsentSensitiveDetails` structurally via **EF Core global query filters** on the `DbContext`, keyed by the current caller's role. Bypassing requires an explicit, greppable `IgnoreQueryFilters()` call — a reviewable act, not an accident.
3. Add a `SensitiveDataAccessLog` table and write an entry on every read of `DbsCheck` or `ConsentSensitiveDetails` (who, what, when, view vs export). **Write mechanism:** a `DbCommandInterceptor` ([EF Core interceptors](https://learn.microsoft.com/ef/core/logging-events-diagnostics/interceptors)) registered globally on the `DbContext`, which detects commands against these two tables and writes the audit row after successful execution — structural, not a per-repository convention that can be forgotten.
4. `SensitiveDataAccessLog` has `UPDATE`/`DELETE` denied at the database permission level (not just an application-level "append-only" convention), and is retained 6 years regardless of the referenced record's own retention.

## Alternatives considered

- **Per-column masking via a query filter** (the original decision) — rejected: technically impossible for the reason above; kept here as a record of what was wrong, not as a live option.
- **Per-call data-access-layer policy checks** — rejected: correct in principle, but relies on every future query author remembering to apply it; not structural.
- **Database-level column encryption** (e.g. Always Encrypted) — rejected for v1: real operational complexity (key management, limited queryability) disproportionate to a solo-maintained, tens-of-users system; the entity-split + query-filter approach gives equivalent access control for the actual threat model here (accidental over-exposure within the application, not a compromised database administrator).
- **A mandatory repository wrapper method for the audit log write** — rejected in favour of the `DbCommandInterceptor`: a wrapper is opt-in by construction (a new query path could bypass it); the interceptor attaches at the `DbContext` level and cannot be bypassed without the same explicit `IgnoreQueryFilters()`-style deliberate act.

## Consequences

- All EF Core queries against `DbsCheck`/`ConsentSensitiveDetails` go through the shared `DbContext` with filters and the audit interceptor applied; any new reporting/export feature is safe and logged by default.
- Any code needing both consent status and sensitive details (there is currently none identified) must join `ConsentRecord` and `ConsentSensitiveDetails` explicitly and will be denied the latter unless the caller's role permits it.
- The read-audit log adds write volume proportional to how often sensitive data is viewed — negligible at tens-of-users scale — and its own 6-year retention is independent of the referenced record's retention.
- OWASP ASVS 5.0's access-control and logging chapters should be checked against this mechanism at build/test time (`quality/test-plan.md` TC-007/TC-008/TC-011), not just assumed satisfied by this ADR.
