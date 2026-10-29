# 0005 - Hosting pivot: self-hosted VPS + PostgreSQL

## Status

Accepted. Partially supersedes `adr/0001-technology-stack.md` (hosting, database, identity, file storage, email). Amends `adr/0003-scheduled-jobs-webjobs.md` (execution host only). Requires a follow-up implementation change to `adr/0004-sensitive-data-access-control.md` §4 (DB engine changes; the access-control/audit *design* does not).

## Context

`adr/0001-technology-stack.md` accepted Azure App Service (B1) + Azure SQL Database (Basic/S0) + Microsoft Entra External ID + Azure Blob Storage + Azure Communication Services Email on the framing "no hard budget ceiling, but a stated preference for low ongoing hosting cost." That framing was never checked against real prices.

A full cost review (`docs/design/hosting-cost-review-2026-07-20.md`) found the accepted stack costs **~$18–30/month in perpetuity at list price**. This system is built for, and its hosting bill is paid by, Shrewsbury Orthodox Church, which operates a chapter of Varangians (corrected 29/10/2026: Varangians is not merely the church's youth club). The review also investigated whether Microsoft's nonprofit program's separate $2,000/year Azure Sponsorship grant covered this — **the owner has confirmed the organisation does not hold that grant** (only the unrelated Microsoft 365 nonprofit tenant exists). Self-funding a recurring cloud bill for a solo-maintained, tens-of-users system, when a materially cheaper and still-compliant option exists, is not justified.

The review also found, independently of cost, that Microsoft Entra External ID requires a brand-new dedicated tenant that cannot be attached to the organisation's existing workforce tenant — real operational overhead ADR-0001 did not account for, since it never priced identity as a cost driver (it's free either way under 50,000 MAU).

**The only hard constraints carried forward:** the documented business/functional requirements, including the whole-entity access-control/audit/retention model, must still be met, and the implementation remains .NET.

## Decision

Replace the following rows of `adr/0001-technology-stack.md`'s stack table:

| Concern | Old (ADR-0001) | New (this ADR) |
|---|---|---|
| Hosting | Azure App Service (Linux, B1) | Self-hosted Linux VPS, Kestrel behind [Caddy](https://caddyserver.com/) (free automatic HTTPS via Let's Encrypt) |
| Database | Azure SQL Database (Basic/S0) | Self-hosted PostgreSQL, same VPS |
| Identity | Microsoft Entra External ID (CIAM) | Self-rolled ASP.NET Core Identity, built-in TOTP MFA |
| File storage | Azure Blob Storage | Local disk on the VPS, off-box backup to an S3-compatible free tier (Cloudflare R2 or Backblaze B2 — final pick at implementation time) |
| Email | Azure Communication Services Email | SMTP — either the organisation's existing Microsoft 365 mailbox (authenticated SMTP submission) or a free-tier transactional provider (e.g. Brevo, Resend) — final pick at implementation time, whichever has a durable free allowance at this volume |
| Scheduled jobs | Azure App Service WebJob (`adr/0003`) | systemd timer / cron on the same VPS, invoking the existing `Vlms.Jobs` console app unchanged — amends *where* it runs, not `adr/0003`'s "plain console app, not a scheduler SDK" reasoning |

**Unchanged from ADR-0001:** Client (Blazor Web App, Server interactivity) and PDF generation (QuestPDF).

Database backup: `pg_dump` on a cron schedule, shipped to the same off-box object storage as file backups. TLS/cert renewal is handled automatically by Caddy.

## Alternatives considered

- **Keep Azure as designed, grant-funded** — rejected: no active Azure nonprofit credit grant exists for this organisation (confirmed by the owner); self-funding ~$18–30/month forever for a charity is not justified when Option C below is materially cheaper and equally compliant.
- **Self-hosted VPS + SQLite** (`hosting-cost-review-2026-07-20.md` Option B) — rejected: SQLite has no database-level user/permission model at all, so it cannot satisfy `adr/0004` §4's "tamper protection at the database permission level, not an application convention" requirement without inventing an untested compensating control. PostgreSQL provides the direct equivalent (`REVOKE ... FROM PUBLIC`) at the same (zero) marginal cost, so there is no cost reason to accept the weaker option.
- **Managed PostgreSQL add-on** (e.g. a $15/month DigitalOcean Managed Database) — rejected: roughly triples this option's cost for automated backup/failover that a cron `pg_dump` to free-tier object storage already provides at this data volume; not justified at tens-of-users scale.

## Consequences

- **`adr/0001-technology-stack.md`** is now partially superseded — its Status line is updated to point here. The client and PDF rows remain in effect unchanged.
- **`adr/0003-scheduled-jobs-webjobs.md`** is amended, not superseded — `Vlms.Jobs` remains a plain console app; only its trigger mechanism changes from the Azure WebJobs folder convention to a systemd timer.
- **`adr/0004-sensitive-data-access-control.md` §4's mechanism must be re-implemented for PostgreSQL.** The structural design (EF Core global query filters restricting `DbsCheck`/`ConsentSensitiveDetails`, the `IMaterializationInterceptor` read-audit trail) is a Domain/EF Core concern and is **provider-independent — it does not change.** Only the database-level tamper-protection DDL changes dialect: `DENY UPDATE, DELETE ON dbo.SensitiveDataAccessLogs TO public` (SQL Server) becomes `REVOKE UPDATE, DELETE ON sensitive_data_access_log FROM PUBLIC` (PostgreSQL) — the same structural guarantee, ported not redesigned. This is tracked as an implementation item, not decided further here.
- **`governance/raid.md` D-002** (email provider/verified sending domain) and **D-004** (least-privilege database connection identity) both need rewording to be provider-generic — tracked as a documentation follow-up alongside the code change, not decided further here.
- **The Microsoft Graph guardian-invitation blocker** (`STATE.md` "Blocked / needs decision" — blocked on a live external Entra tenant) is **retired, not merely unblocked**: a self-rolled Identity invite flow (an emailed, signed, single-use token link scoped to the correct `Student` record) satisfies `design/data-design.md`'s "guardian link verification" requirement without any tenant or Graph dependency at all.
- **Genuine new operational burden, named rather than hidden:** OS security patching, systemd unit maintenance, PostgreSQL backup *verification* (a backup that has never been test-restored is not a backup), and TLS renewal monitoring are all now owner-carried — there is no managed platform absorbing this the way Azure App Service did. `governance/raid.md` R-002 (solo-maintainer bus-factor risk) is unchanged in kind but now has no vendor SLA underneath it.
- **VPS provisioning itself (account creation, payment method, provider/region choice) is a real-world action with a real (if small) cost and is not to be taken unilaterally** — it requires the owner's explicit go-ahead before any account is created or resource is provisioned, separately from the code-level implementation work this ADR authorises.
- Test suite implication (tracked as an implementation item, not decided further here): the existing SQLite-in-memory-via-DI test pattern (`tests/Vlms.Tests/`) was always a test double, not the production engine, even before this ADR — whether to keep it as-is or move to a real PostgreSQL instance in tests (e.g. via Testcontainers) so the tamper-protection DDL and Postgres-specific behaviour are actually exercised is an open implementation decision.

## Amendment — VPS provider chosen (12/10/2026)

Philip chose **DigitalOcean, London region (LON1)**, the UK-resident option in `hosting-cost-review-2026-07-20.md` §4.2. This removes the data-residency question for the church. Also decided by Philip 12/10/2026: the app is served at **www.varangians.co.uk**, a domain dedicated to the youth club and not used for anything else (the church has its own separate address; Philip controls the domain's DNS; registered with Hostinger, DNS only — no Hostinger web hosting, the Droplet is the host), and off-box backups go to **Cloudflare R2** (S3-compatible; `AWS_DEFAULT_REGION=auto`, per `deploy/backup/pg-backup.sh`). No account or resource has been created; provisioning still needs Philip's explicit go-ahead.

**Email domain — Philip, 12/10/2026:** `varangians.co.uk` is added to the church's Microsoft 365 tenant (MX, SPF, DKIM live; DMARC `p=none` with reports to `dmarc@varangians.co.uk`, an alias on Philip's mailbox — tighten to `p=quarantine` once reports show clean mail). The app will send from the shared mailbox `noreply@varangians.co.uk`. Teachers will also have `@varangians.co.uk` mailboxes, so the app's send permission must be scoped to that one mailbox by identity, never to the whole domain. **Sending mechanism — Philip, 12/10/2026: Microsoft Graph `sendMail`, app-only, scoped by Exchange RBAC for Applications.** This replaces the "SMTP" row's means, not its need: SMTP basic auth to Microsoft 365 is being retired, and app-only SMTP would also need SMTP AUTH enabled (blocked by security defaults). Set up in the church tenant (`9abc26d2-cda5-428d-a8ad-ccfbb0788b9a`) on 12/10/2026:
- Entra app "Varangians LMS mailer", single-tenant, app ID `e74439c2-2ada-4204-83b9-d0e686b8675f`, enterprise-app object ID `2baf649e-9329-4843-9afc-c2a8f90110a3`. **No Entra API permissions** (no tenant-wide `Mail.Send`). No secret yet: it is created at go-live and written straight into the server's environment files, never displayed or committed.
- Exchange: service principal registered; management scope "Varangians LMS mailer - noreply only" (`PrimarySmtpAddress -eq 'noreply@varangians.co.uk'`); role assignment `Application Mail.Send` on that scope. `Test-ServicePrincipalAuthorization`: in scope for `noreply@varangians.co.uk`, out of scope for another mailbox.
- Exchange caches app permission changes for 30 minutes to 2 hours (Microsoft Learn, RBAC for Applications), so a first send may fail shortly after any change.

**Droplet size — decided by Philip 12/10/2026:** Basic 2 GiB / 1 vCPU / 50 GiB SSD at $12/month rather than the review's 1 GiB at $6/month, because PostgreSQL, the Blazor Server app (which holds a live circuit in memory per connected user) and the nightly jobs share one machine; 1 GiB leaves little headroom. Prices from [DigitalOcean Droplet pricing](https://www.digitalocean.com/pricing/droplets), checked 12/10/2026.

## Reference

Full cost analysis, pricing sources, and the ranked comparison behind this decision: `docs/design/hosting-cost-review-2026-07-20.md`.
