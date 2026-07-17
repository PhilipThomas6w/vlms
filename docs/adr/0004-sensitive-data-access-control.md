# 0004 - Sensitive data access control: EF Core global query filters + read audit log

## Status

Accepted

## Context

VLMS stores special-category and children's personal data directly (`DbsCheck`, `ConsentRecord` sensitive fields) with a confirmed, role-specific access model: `DbsCheck` is restricted entirely to Admin/Safeguarding Officer; `ConsentRecord`'s medical/dietary-SEN/emergency-contact fields are masked from Teacher and Approver (`data-design.md`, `governance/security-compliance.md`). A design review of this project raised two related risks: (1) if masking/restriction is implemented as an opt-in convention that each new query/report must remember to apply, one missed call site leaks sensitive data; (2) the original design logged who *created or approved* a safeguarding record, but nothing logged who subsequently *read* one — for a system whose core justification is protecting this data, an absent read-audit trail is a real control gap, not a documentation gap.

## Decision

1. Enforce column masking and whole-record restriction structurally via **EF Core global query filters** configured on the `DbContext`, keyed by the current caller's role. A developer cannot accidentally bypass this by writing a new query — bypassing requires an explicit, greppable `IgnoreQueryFilters()` call, which is itself a reviewable act.
2. Add a `SensitiveDataAccessLog` table (`data-design.md`) and write an entry on every read of a `DbsCheck` record or a `ConsentRecord`'s masked fields (who, what, when, view vs export).

## Alternatives considered

- **Per-call data-access-layer policy checks** (the original design) — rejected: correct in principle, but relies on every future query author remembering to apply it; not structural.
- **Database-level column encryption** (e.g. Always Encrypted) — rejected for v1: adds real operational complexity (key management, limited queryability of encrypted columns) disproportionate to a solo-maintained, tens-of-users system; the EF Core filter approach gives equivalent access control for the actual threat model here (accidental over-exposure within the application, not a compromised database administrator).

## Consequences

- All EF Core queries against `DbsCheck`/`ConsentRecord` must go through the shared `DbContext` with the filters applied; any new reporting/export feature is safe by default.
- The read-audit log adds write volume proportional to how often sensitive data is viewed — negligible at tens-of-users scale.
- OWASP ASVS 5.0's access-control and logging chapters should be checked against this mechanism at build/test time (`quality/test-plan.md` TC-011/012), not just assumed satisfied by this ADR.
