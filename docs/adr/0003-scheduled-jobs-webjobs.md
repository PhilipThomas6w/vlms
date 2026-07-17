# 0003 - Scheduled/background work via Azure App Service WebJobs

## Status

Accepted

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
