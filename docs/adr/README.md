# Architecture Decision Records

Status: in progress — 6 ADRs recorded (see below).

ADRs are added here as `NNNN-<slug>.md` in Nygard format (Context, Decision, Status, Consequences) as decisions are made during design.

- `0001-technology-stack.md` — Blazor + Azure stack.
- `0002-roles-as-application-claims.md` — user roles as application claims, not native Entra External ID groups.
- `0003-scheduled-jobs-webjobs.md` — background/scheduled work via Azure App Service WebJobs.
- `0004-sensitive-data-access-control.md` — `ConsentRecord`/`ConsentSensitiveDetails` entity split + EF Core global query filters + `IMaterializationInterceptor`-based read audit log for `DbsCheck`/`ConsentSensitiveDetails`.
- `0005-hosting-pivot-self-hosted-vps-postgresql.md` — self-hosted Linux VPS + PostgreSQL + ASP.NET Core Identity + local disk storage; partially supersedes 0001, amends 0003.
- `0006-parent-consent-and-teacher-read-access.md` — parents submit the annual consent form; `ConsentSensitiveDetails` also readable by Teachers and by the student's linked Parents (amends 0004; `DbsCheck` unchanged).
