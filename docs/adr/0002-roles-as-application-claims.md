# 0002 - User roles modelled as application claims, not native Entra External ID groups

## Status

Accepted

## Context

VLMS uses Microsoft Entra External ID for authentication (`adr/0001-technology-stack.md`). Six roles exist: Admin, Teacher, Approver, Parent, Student, Safeguarding Officer (`requirements/stakeholders.md`). Some authorization decisions are pure role checks (e.g. only Safeguarding Officer/Admin may view `DbsCheck`), but others are resource-scoped and role alone cannot express them: a Parent's access is scoped to specific `Student` records via `StudentGuardianLink`, not just "is a Parent" (`data-design.md`).

## Decision

Model roles as an application-level `UserRole` table (`AppUser` 1:N `UserRole`), enforced through ASP.NET Core authorization policies plus resource-based handlers — not as native Entra External ID group membership.

## Alternatives considered

- **Native Entra External ID groups**, mapped 1:1 to the six roles — rejected: groups express "is a member of role X" well, but cannot express "is a Parent of *this specific* Student", which is central to the access model (a Parent must never see another parent's child's medical/consent data). Resource-scoped authorization has to live in the application regardless, so keeping role membership there too avoids splitting the access-control model across two systems.

## Consequences

- Role and resource-scope checks live in one place (`Vlms.Web` authorization policies + handlers), which is easier for a solo maintainer to reason about and test than a split model.
- A user's role is not visible/manageable directly from the Entra admin center — role assignment is an application feature (Admin UI), which must itself be access-controlled (only Admin can assign roles).
- If the user base later grows enough that centralised (Entra-level) role governance becomes valuable, this can be revisited without changing the data model — `UserRole` could be populated from Entra group claims instead of an app-managed table.
