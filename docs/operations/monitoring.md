# Monitoring, alerting and uptime (VLMS-MON)

The design replaced the original Application Insights plan with journald on the host (`adr/0003` as amended by `adr/0005`). There is no metrics or APM stack. At this scale (tens of users, best-effort availability) the plan is journald, one external uptime check, and a weekly look.

## 1. Where logs go

| Source | Destination | Read with |
|---|---|---|
| `Vlms.Web` (console `ILogger`) | journald, identifier `vlms-web` | `journalctl -u vlms-web.service` |
| `Vlms.Jobs` | journald | `journalctl -u vlms-jobs.service` |
| Backups | journald | `journalctl -u vlms-backup.service -u vlms-files-backup.service` |
| Caddy process | journald | `journalctl -u caddy` |
| HTTP access log | `/var/log/caddy/vlms-web-access.log` (JSON) | `tail`, `jq` |
| PostgreSQL | journald / `/var/log/postgresql/` | `journalctl -u postgresql` |
| Safeguarding-data reads | database table `sensitive_data_access_logs` (append-only, RLS) | Admin/SO in-app, or SQL as superuser |

Log levels: `Information` by default, `Warning` for `Microsoft.AspNetCore` and EF Core (`appsettings.json`).

Personal data in logs: the escalation lines below include recipient emails and student/teacher names. That is why the journal retention is capped (§2) and why journal exports are handled like the database.

## 2. journald retention

Ubuntu keeps the journal on disk (`/var/log/journal`). Default size cap: 10% of the filesystem, at most 4 GiB ([`journald.conf(5)`](https://www.freedesktop.org/software/systemd/man/latest/journald.conf.html)). Set an explicit cap in `/etc/systemd/journald.conf.d/10-vlms.conf`:

```
[Journal]
SystemMaxUse=1G
MaxRetentionSec=90day
```

then `sudo systemctl restart systemd-journald`. Values: `[TBC – Philip]`. The suggested 90 days covers a term's worth of incident lookback while keeping logged personal data short-lived. The 6-year retention for the safeguarding read audit is met by the database table, not the journal.

Caddy access log: rotated by Caddy itself (default 100 MiB per file, keeps 10 files and 90 days; [Caddy `log` directive](https://caddyserver.com/docs/caddyfile/directives/log)). No extra configuration needed.

## 3. What needs attention

| Signal | Meaning | Find it |
|---|---|---|
| `ESCALATION: safeguarding-critical notification ... failed` (Error) | A consent/DBS email could not be sent after all retries | `journalctl -u vlms-web.service -u vlms-jobs.service -g ESCALATION --since -7d` |
| `ESCALATION: student ... has no valid consent` / `teacher ... has no valid DBS` | Expiry sweep found a lapsed record | same search |
| `ConsentExpiryJob run failed.` (Critical), non-zero exit | Nightly sweep did not run | `systemctl status vlms-jobs.service` |
| Unit in `failed` state | Any job or backup failed | `systemctl --failed` |
| `vlms-web.service` restart loop | App crashing at start (often config) | `journalctl -u vlms-web.service -b` |
| Graph `401`/`invalid_client` | Email secret expired or wrong | `journalctl -u vlms-web.service -g Graph` |
| Disk over 80% | Risk to PostgreSQL and backups | `df -h /` |

These escalations are written to the log only, so they reach nobody until someone looks. Choosing an alert route is the main open monitoring decision:

- **Alert route:** `[TBC – Philip]`. Options, cheapest first:
  1. Weekly manual check (§3 searches) and nothing automated. Acceptable for best-effort availability, but a failed safeguarding email could then go unnoticed for up to a week.
  2. A small systemd `OnFailure=` unit plus a daily timer that emails Philip any `ESCALATION:` lines or failed units. The app's own Graph mailer can't be reused from a shell script without more code, so this needs a code item.
  3. An external log service. Not recommended: more cost, and personal data leaves the box.

## 4. Backup checks

```sh
systemctl list-timers 'vlms-*'                        # last and next run of each timer
journalctl -u vlms-backup.service --since today
aws s3 ls s3://<bucket>/postgres/ --endpoint-url <r2-endpoint> | tail -3   # newest dump is from last night
```

Run as a user that has `backup.env` loaded, or with `systemd-run` as in `deploy/README.md` §9.

## 5. Uptime

- **External uptime check:** `[TBC – Philip]`, e.g. a free-tier HTTP monitor (UptimeRobot, Better Stack) or DigitalOcean's uptime checks, polling `https://www.varangians.co.uk/Account/Login` every 5 minutes and emailing Philip on failure. The app has no dedicated health endpoint. The login page exercises Caddy, TLS and Kestrel but not the database; adding `/health` with a database check is an optional code item.
- **Certificate expiry:** Caddy renews automatically. The uptime monitor's TLS-expiry warning (where offered) is the backstop.
- **DigitalOcean monitoring agent** (free CPU/memory/disk graphs and alert policies): `[TBC – Philip]`. Recommended at least for a disk-usage alert.
