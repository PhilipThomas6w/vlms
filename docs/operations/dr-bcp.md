# Backup, disaster recovery and continuity (VLMS-BCP)

## 1. Targets

`requirements/non-functional.md` sets no formal SLA, RPO or RTO ("best-effort"). The figures below are what the current design actually delivers, stated so Philip can accept or change them: `[TBC – Philip: accept as targets]`.

| Measure | Delivered by the design | Basis |
|---|---|---|
| RPO (data loss) | up to 24 hours | one `pg_dump` and one file sync per night (02:00 / 02:30 UTC) |
| RTO (time to restore) | about half a day of operator time, once the operator is available | full rebuild per §3; no standby server |
| Availability | single VPS, no redundancy | `adr/0005` |

A 24-hour RPO means completions, consent records or DBS checks entered during the day of a total loss must be re-entered from paper or memory. Teachers should know this.

## 2. What is backed up

| Data | Backup | Where | Retention |
|---|---|---|---|
| PostgreSQL `vlms` database (everything incl. audit log and Identity) | `pg_dump -Fc` nightly 02:00 UTC (`deploy/backup/pg-backup.sh`) | R2 `s3://<bucket>/postgres/`; local copy in `/var/backups/vlms` | local 7 days (`LOCAL_RETENTION_DAYS`); R2 `[TBC – Philip]` (bucket lifecycle rule, not set by the script) |
| Certificate PDFs (`Storage__LocalRootPath`) | `aws s3 sync`, no delete, nightly 02:30 UTC (`files-backup.sh`) | R2 `s3://<bucket>/files/` | indefinite; bucket versioning recommended (`deploy/README.md` §7) |
| Configuration and code | git repository | GitHub `PhilipThomas6w/vlms` plus local clones | git history |
| Secrets (`/etc/vlms/*.env`) | **not backed up** | recreated (§3) | n/a |
| DataProtection keys | **deliberately not backed up** | n/a | losing them signs everyone out and voids open invite links (`deploy/README.md` §1) |
| Caddy certificates | not backed up | reissued automatically | n/a |

Backup retention and data retention interact: a deleted student remains in older dumps until those dumps expire. The R2 lifecycle period should therefore be short and written into the privacy notice: `[TBC – Philip]`, e.g. 35 days.

Database backup is from the same night as the file backup, so a restore of both is consistent to within 30 minutes.

## 3. Rebuild from scratch (Droplet lost or compromised)

1. Create a new Droplet (same size and region) and point both DNS A records at it. Hostinger TTL decides how quickly users follow.
2. `host-hardening.md` in full.
3. `deploy/README.md` §1–§2 (user, directories, PostgreSQL, Caddy, .NET, aws CLI).
4. §3 step 1 only (roles and database), set the two role passwords, then **restore instead of migrating**:
   ```sh
   aws s3 cp s3://<bucket>/postgres/<latest>.dump /tmp/ --endpoint-url <r2-endpoint>
   sudo -u postgres pg_restore -d vlms --no-owner --role=vlms_owner /tmp/<latest>.dump
   sudo -u postgres psql -d vlms -f deploy/postgres/10-grant-app-privileges.sql
   ```
   Then run `migrate.sh` (a no-op if the dump is current) and the §3 RLS checks. Exact restore flags are confirmed by the backup restore test (`STATE.md`) before this procedure is relied on: `[TBC – restore test]`.
5. Recreate `/etc/vlms/web.env`, `jobs.env`, `backup.env`, `migrate.env` from the password manager `[TBC – Philip: where secrets are kept]`. The Graph client secret can be reissued (`deploy/README.md` §4).
6. Publish the release that matches the dump's last migration (`release.md` §2), install the units (§5) and the Caddyfile (§6).
7. Restore files: `deploy/README.md` §9.
8. Run the `deploy/README.md` §7 checklist. Tell users they need to sign in again.

If the cause was a compromise: rotate every credential (both role passwords, R2 keys, Graph secret, admin SSH keys), and treat it as a possible breach (`runbook.md` §5 step 4).

## 4. Testing

- Before go-live and then quarterly: restore the latest dump into a scratch database and the file store into a scratch directory, and spot-check (`STATE.md` "Backup restore test"; `deploy/README.md` §7).
- Record each test (date, dump used, result, time taken) in `[TBC – Philip: test log location]`. The time taken is the evidence for the RTO above.

## 5. Continuity (people and accounts)

The largest continuity risk is the single operator, not the server.

- Accounts needed to run or recover VLMS: DigitalOcean, Cloudflare (R2), Hostinger (DNS and domain renewal), Microsoft 365 tenant (Graph mailer, mailboxes), GitHub. All are currently held by Philip.
- Break-glass: `[TBC – Philip]`, e.g. a sealed record of how to reach these accounts, held by a named person at the church, plus this pack.
- Domain renewal date: `[TBC – Philip]`. An expired domain takes the service and its email down.
- If VLMS is unavailable for a long period, sessions carry on and progress is recorded on paper, then entered once it is back. Consent checks before activities fall back to the paper forms the club holds `[TBC – Philip: confirm paper fallback exists]`.
