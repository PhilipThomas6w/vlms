# Operational runbook (VLMS-RUN)

## 1. System at a glance

| Component | Where | Runs as | Unit / file |
|---|---|---|---|
| Web app (`Vlms.Web`, Blazor Server) | Kestrel on `127.0.0.1:5000` | `vlms` | `vlms-web.service` |
| Reverse proxy + TLS | Caddy, ports 80/443, `www.varangians.co.uk` (bare domain redirects) | `caddy` | `/etc/caddy/Caddyfile` (from `deploy/Caddyfile`) |
| Database | PostgreSQL on the same VPS, database `vlms` | app connects as `vlms_app`; migrations as `vlms_owner` | `postgresql.service` |
| Consent/DBS expiry sweep + safeguarding email | `Vlms.Jobs`, daily 03:00 UTC | `vlms` | `vlms-jobs.timer` → `vlms-jobs.service` |
| Database backup (`pg_dump` → Cloudflare R2) | daily 02:00 UTC | `vlms` | `vlms-backup.timer` → `vlms-backup.service` |
| File-storage backup (certificate PDFs → R2) | daily 02:30 UTC | `vlms` | `vlms-files-backup.timer` → `vlms-files-backup.service` |
| Outbound email | Microsoft Graph as `noreply@varangians.co.uk` | Entra app "Varangians LMS mailer" | `GraphMail__ClientSecret` in `web.env`/`jobs.env` |

Host: DigitalOcean Droplet, London (LON1), Basic 2 GiB / 1 vCPU / 50 GiB. OS image: `[TBC – Philip]` (the deployment scripts assume a Debian/Ubuntu-family distribution; an Ubuntu LTS release is the working assumption). DNS: A records for `varangians.co.uk` and `www` in Hostinger.

All times in this pack are **UTC** (see `README.md`).

## 2. Support model

- **Operator and sole maintainer:** Philip. There is no on-call rota and no out-of-hours cover, consistent with the best-effort availability NFR (`requirements/non-functional.md`).
- **Users report problems to:** `[TBC – Philip]` (an address or channel teachers and parents can use).
- **Backup operator if Philip is unavailable:** `[TBC – Philip]`. Without one, the service waits until Philip is back (see `dr-bcp.md` §5).
- **Priority:** anything affecting safeguarding data, consent-expiry blocking, or a suspected data breach comes first; everything else is fixed in the next available session.

## 3. Routine operations

| Frequency | Task | How |
|---|---|---|
| Daily (automatic) | Database and file backups, expiry sweep | Timers; check with `systemctl list-timers 'vlms-*'` |
| Weekly | Check for failed units and escalations | `systemctl --failed`; `journalctl -p err --since -7d` and the `ESCALATION:` search in `monitoring.md` §3 |
| Weekly | Confirm last night's backups reached R2 | `monitoring.md` §4 |
| Monthly | Disk space and journal size | `df -h /`, `journalctl --disk-usage` |
| Monthly | Pending OS updates and reboot-required flag | `host-hardening.md` §3 |
| Quarterly | Test-restore a database dump and the file store to scratch | `dr-bcp.md` §4 |
| Before 2-year expiry | Rotate the Graph client secret | `deploy/README.md` §4 "Email secret"; first expiry date `[TBC – set when the secret is created at go-live]` |
| Annually | Review ASVS/WCAG checklists, access list, this pack | `docs/governance/`, `docs/quality/` |

## 4. Common procedures

### Service status and restart

```sh
systemctl status vlms-web.service caddy postgresql
sudo systemctl restart vlms-web.service       # signs nobody out: DataProtection keys persist in /var/lib/vlms/keys
journalctl -u vlms-web.service -n 100 --no-pager
```

A restart drops live Blazor circuits; users see a reconnect banner and carry on. Prefer to do it outside session times (evenings and weekends).

### Run the expiry sweep on demand

```sh
sudo systemctl start vlms-jobs.service
journalctl -u vlms-jobs.service -n 50 --no-pager   # expect "ConsentExpiryJob completed: ..."
```

### Add or change a user's role

In the app: `/administration/user-roles` (Admin only, MFA required). Guardian invite acceptances show in the waiting-for-approval list and are approved there by granting `Parent`. There is no shell route for role changes apart from the one-off first-Admin bootstrap (`deploy/README.md` §6a).

### Seed or update the curriculum

`Vlms.Jobs seed-curriculum --manifest <path> --content-root <dir>` upserts the ranks, lessons, tags and lesson files from `seed/curriculum.json` and the lesson content folders. Idempotent, never deletes (extra lessons are reported as warnings), and writes nothing if validation fails. Needs the migrated database and the production `Storage__LocalRootPath` in `/etc/vlms/jobs.env`. Exact command: `deploy/README.md` §6b.

### A user is locked out or lost their authenticator

- Wrong password five times locks the account for 15 minutes (`VlmsIdentityPolicy`). Wait, or use "Forgot your password?".
- Lost authenticator device: the user signs in with a recovery code. If they have none, there is no in-app admin reset of two-factor authentication. Procedure: `[TBC – Philip]` (direct database change by the operator, or a feature to add).

### Mark a leaver

Students and teachers are flagged as leaving in the app (manual "leaving" flag). Flagged records appear on the "Records due for deletion" report once their retention period has passed. The purge itself is not yet built (`STATE.md` Blocked: retention purge).

## 5. Incident first steps

1. **Site down (no response or a Caddy 502):** `systemctl status vlms-web.service`; read the last 100 journal lines; check `df -h` and `free -m`; `sudo systemctl restart vlms-web.service`. If PostgreSQL is down, start it and check its log (`journalctl -u postgresql`).
2. **Certificate error:** `journalctl -u caddy -n 200`; confirm DNS still points at the Droplet and ports 80/443 are open (`host-hardening.md` §2).
3. **Email not arriving:** search the journal for Graph errors (`journalctl -u vlms-web.service -g Graph`). A `401`/`invalid_client` usually means the client secret has expired; rotate it.
4. **Suspected data breach or unauthorised access to safeguarding data:** preserve evidence first (do not restart or delete anything), export `sensitive_data_access_logs` and the journal, then follow `governance/security-compliance.md`. Whether and how to report to the ICO within 72 hours: `[TBC – Philip]` (ICO question open in `STATE.md` Blocked).
5. **Anything requiring a rebuild:** `dr-bcp.md` §3.

Record every incident (date, impact, cause, fix) in `[TBC – Philip: incident log location]`.
