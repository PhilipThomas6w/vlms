# Architecture Decision Records

Status: in progress — 4 ADRs recorded (see below).

ADRs are added here as `NNNN-<slug>.md` in Nygard format (Context, Decision, Status, Consequences) as decisions are made during design.

- `0001-technology-stack.md` — Blazor + Azure stack.
- `0002-roles-as-application-claims.md` — user roles as application claims, not native Entra External ID groups.
- `0003-scheduled-jobs-webjobs.md` — background/scheduled work via Azure App Service WebJobs.
- `0004-sensitive-data-access-control.md` — EF Core global query filters + read audit log for `DbsCheck`/`ConsentRecord`.
