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

then `sudo systemctl restart systemd-journald`. Values: 90 days (Philip, 27/10/2026). The suggested 90 days covers a term's worth of incident lookback while keeping logged personal data short-lived. The 6-year retention for the safeguarding read audit is met by the database table, not the journal.

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

These escalations are written to the log only, so the alert route below carries them to a person.

- **Alert route — decided (Philip, 27/10/2026), built 27/10/2026:**
  1. `Vlms.Jobs alert-digest` (`AlertDigestService`) searches the journal of `vlms-web.service` and `vlms-jobs.service` for `ESCALATION:` lines, and lists failed `vlms-*` units, since its last successful run. If it finds anything it sends one email through the app's Graph mailer to `Alerts:Recipient` (default `alerts@varangians.co.uk`, set as `Alerts__Recipient` in `/etc/vlms/jobs.env` to change). Nothing found, nothing sent. `vlms-alert.timer` runs it at 07:30 UTC daily; every `vlms-*` service unit has `OnFailure=vlms-alert.service`, so a failed job or backup is reported at once. Details and behaviour:
     - The time of the last successful run, and the failed units already reported, are kept in `alert-state.json` under `/var/lib/vlms-alert`. They move forward only after the email was accepted, so a failed run covers the same period next time. The first run looks back 24 hours.
     - A unit that stays failed is reported once, not daily; run `systemctl reset-failed <unit>` after fixing it. A unit that fails again while still in the failed state is not re-reported (the weekly look is the backstop).
     - The email carries the journal lines as they are (names and recipient addresses already in the journal), capped at 200 lines and 60,000 characters, with a note of what was left out.
     - If the host cannot be read or the email cannot be sent, the command logs an error and exits non-zero, so `vlms-alert.service` shows in `systemctl --failed`. It is never reported as "nothing found".
     - The unit has `systemd-journal` for itself only, so `journalctl` can read the web and jobs entries; the web process does not get it.
     - Install and test: `deploy/README.md` §5a.
  2. `alerts@varangians.co.uk` is an M365 address forwarding to Philip (he creates it); more recipients are added there, not in the app.
  3. A weekly look at the §3 searches stays as the backstop.
  4. Rejected: an external log service (cost, and personal data would leave the box).

## 4. Backup checks

```sh
systemctl list-timers 'vlms-*'                        # last and next run of each timer
journalctl -u vlms-backup.service --since today
aws s3 ls s3://<bucket>/postgres/ --endpoint-url <r2-endpoint> | tail -3   # newest dump is from last night
```

Run as a user that has `backup.env` loaded, or with `systemd-run` as in `deploy/README.md` §9.

## 5. Uptime

- **External uptime check — decided (Philip, 27/10/2026):** UptimeRobot free plan polling `https://www.varangians.co.uk/health` every 5 minutes, alerting `alerts@varangians.co.uk` on failure and on certificate expiry. `/health` is built (`src/Vlms.Web/HealthEndpoint.cs`): anonymous, no antiforgery, it opens a database connection (five-second limit) and answers 200 `Healthy` or 503 `Unhealthy`, nothing else in the body, no exception text. So the check covers Caddy, TLS, Kestrel and PostgreSQL. A crash loop of `vlms-web.service` is caught here rather than by `OnFailure=`, because `Restart=always` with a 10-second delay never reaches the start-limit that would mark the unit failed.
- **Certificate expiry:** Caddy renews automatically. The uptime monitor's TLS-expiry warning (where offered) is the backstop.
- **DigitalOcean monitoring agent — decided (Philip, 27/10/2026):** installed, with an alert policy at 80% disk use emailing `alerts@varangians.co.uk`.
