# 0003 - Scheduled/background work via Azure App Service WebJobs

## Status

Amended by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` — execution host changes from an Azure App Service WebJob to a systemd timer/cron on the self-hosted VPS. The decision below ("a plain console app, not a dedicated scheduler SDK") is unchanged.

## Context

VLMS needs recurring background work: daily consent/DBS expiry monitoring with escalation, and at-risk/disengaged student flagging (8-week no-completion threshold) — see `design/low-level-design.md` (`ConsentExpiryJob`). This is the only background/scheduled workload in the system; everything else is synchronous, user-triggered web request handling.

## Decision

Run this as an Azure App Service **WebJob**, co-located with the `Vlms.Web` App Service plan.

## Alternatives considered

- **Azure Functions (Timer trigger)** in a separate Function App — rejected for v1: adds a second Azure resource to provision, monitor, and pay for (per `raid.md` R-002, solo-maintainer bus-factor risk means fewer moving parts is a genuine mitigation, not just convenience), for a single daily job with no independent scaling need at tens-of-users scale.

## Consequences

- The scheduled job shares the App Service plan's compute and lifecycle with the web app — a deployment of `Vlms.Web` also redeploys the WebJob; there is no independent scaling or failure isolation between them.
- If the background workload grows materially (e.g. many more jobs, higher-frequency triggers, or a need to scale independently of the web app), migrating to Azure Functions is a contained change — it does not affect the domain logic in `Vlms.Domain`, only the hosting/trigger mechanism.
- WebJob failures must be monitored explicitly (e.g. Application Insights alerting) since there is no separate Functions-portal execution history to fall back on.

### Amendment (`adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`): consequences under the systemd timer

The paragraphs above described the Azure App Service WebJob's coupling to `Vlms.Web`'s deployment; that coupling does **not** carry over to the systemd timer that replaced it (`deploy/systemd/vlms-jobs.service` + `deploy/systemd/vlms-jobs.timer`):

- `Vlms.Jobs` and `Vlms.Web` are now two independent systemd units on the same VPS, not one App Service plan's co-located WebJob folder. A `Vlms.Web` deployment/restart does **not** redeploy or restart `Vlms.Jobs` — the two are decoupled, which is a genuine improvement in failure isolation over the original WebJob's shared-lifecycle coupling this ADR originally accepted.
- The trade-off moves elsewhere: redeploying a new `Vlms.Jobs` build now requires its own explicit step — publish the new build to the path `vlms-jobs.service`'s `ExecStart`/`WorkingDirectory` point at, then nothing further is needed for the *next scheduled run* to pick it up (systemd re-execs `ExecStart` fresh each time the timer fires; no long-running process to restart). Only a change to the unit/timer *files themselves* (schedule, environment, paths) requires `systemctl daemon-reload` — see `deploy/systemd/README.md`.
- Monitoring moves from Application Insights (never provisioned — no live Azure environment ever existed for this project) to `journalctl -u vlms-jobs.service` plus the process's own non-zero exit code / `LogCritical` on failure (`Program.cs`'s catch block) — systemd surfaces a oneshot service's failed exit status via `systemctl status`/`journalctl` the same way Application Insights would have surfaced a failed WebJob execution.
- Both units still run on the same OS instance, so they are not failure-isolated from VPS-level events (a kernel panic or OS patching reboot affects both) — only App Service-plan-specific deployment coupling is removed, not all shared-host risk. This is the same trade-off `adr/0005`'s Consequences section already names generally (no managed platform absorbing OS-level operational burden).
