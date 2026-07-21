# VLMS self-hosted VPS deployment

End-to-end deployment narrative for `docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`'s
target: Kestrel (`Vlms.Web`) behind [Caddy](https://caddyserver.com/) for automatic HTTPS, a
self-hosted PostgreSQL database on the same VPS, `Vlms.Jobs` on a systemd timer, and a `pg_dump`
backup shipped off-box. STATE.md hosting-pivot item 6 — this file ties together
`deploy/systemd/`, `deploy/Caddyfile`, `deploy/postgres/`, and `deploy/backup/`.

**This is scripts/config only.** Nothing in this repository provisions a real VPS, creates a cloud
account, or spends money — that is a real-world action requiring Philip's explicit go-ahead on
provider/region/budget first (`adr/0005` Consequences), separate from this being ready to run once
that go-ahead is given. Every value below marked PLACEHOLDER must be replaced with a real one at
that point; none of them are real anywhere in this repository.

## 0. Prerequisites, before touching any file in this repository

- A Linux VPS (this deployment assumes a Debian/Ubuntu-family distribution — the exact package
  manager commands below are for `apt`; adapt if a different distribution is chosen) with a public
  IP, SSH access, and enough disk for the .NET runtime + PostgreSQL + certificate-store +
  local-disk blob storage (`src/Vlms.Infrastructure/Storage/LocalDiskBlobStorage.cs`'s
  `App_Data/blobs`) at this system's tens-of-users scale.
- A domain name, with DNS already pointed at the VPS's public IP — required before Caddy's
  automatic HTTPS can succeed (see `deploy/Caddyfile`'s own comments).
- Ports 80 and 443 reachable from the public internet (VPS firewall / cloud provider security
  group) — Caddy needs both for ACME certificate issuance and renewal.
- An off-box S3-compatible object storage account (Cloudflare R2 or Backblaze B2 — **still an
  open, undecided choice per `adr/0005`**, tracked there, not decided by this item) with a bucket
  and an access key pair, for `deploy/backup/pg-backup.sh`.

## 1. OS prep — a non-root deploy/runtime user

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin vlms
sudo mkdir -p /opt/vlms/web /opt/vlms/jobs /opt/vlms/backup /etc/vlms /var/backups/vlms
sudo chown -R vlms:vlms /opt/vlms /var/backups/vlms
```

`vlms` is the same dedicated, unprivileged system user/group every unit in `deploy/systemd/` and
`deploy/backup/` runs as — nothing VLMS-owned ever runs as `root`. Caddy is installed as its own
package-managed service (below) and runs under its own service account, not `vlms`.

## 2. Install PostgreSQL, Caddy, and the .NET runtime

```sh
# PostgreSQL — use your distribution's current package (this deployment has no version pin beyond
# "current PostgreSQL", since deploy/postgres/*.sql was verified against current PostgreSQL
# documentation, not a specific old version).
sudo apt update
sudo apt install -y postgresql

# Caddy — official Cloudsmith repository, verified against caddyserver.com/docs/install rather
# than assumed. Installing this package sets Caddy up as its own systemd service ("caddy")
# automatically and enables it.
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo chmod o+r /usr/share/keyrings/caddy-stable-archive-keyring.gpg /etc/apt/sources.list.d/caddy-stable.list
sudo apt update
sudo apt install -y caddy

# .NET runtime — framework-dependent deployment for Vlms.Web/Vlms.Jobs (see each unit file's own
# comment); follow Microsoft's current install instructions for your distribution
# (learn.microsoft.com/dotnet/core/install/linux) for the ASP.NET Core runtime matching this
# solution's TargetFramework (net10.0 — src/Vlms.Web/Vlms.Web.csproj).

# aws CLI — needed by deploy/backup/pg-backup.sh to ship dumps to the S3-compatible bucket
# (docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html for current instructions).
```

## 3. PostgreSQL: roles, database, least-privilege grants

Read `deploy/postgres/00-create-roles-and-database.sql` and
`deploy/postgres/10-grant-app-privileges.sql` first — both files carry the actual reasoning
(`governance/raid.md` D-004, `docs/adr/0004-sensitive-data-access-control.md` §4 pass 6) for why
this is two roles and two scripts, not one of each. Summary of the order (getting this order wrong
defeats the least-privilege design — re-read the scripts, don't just copy these commands blind):

```sh
# 1. Create vlms_owner (migrator/schema-owner role) + vlms_app (runtime role) + the "vlms" database.
sudo -u postgres psql -f deploy/postgres/00-create-roles-and-database.sql
# — then replace the two PLACEHOLDER passwords inside a live cluster via ALTER ROLE ... PASSWORD,
#   not by editing and re-running this file (CREATE ROLE only runs once, guarded by the DO block).

# 2. Apply EF Core migrations AS vlms_owner — this is what actually creates every table, owned by
#    vlms_owner (never vlms_app — that's the whole point). From a machine with the .NET SDK/dotnet-ef
#    tool and this repository checked out:
dotnet ef database update --project src/Vlms.Infrastructure --startup-project src/Vlms.Web \
  --connection "Host=<vps-host>;Database=vlms;Username=vlms_owner;Password=<the real vlms_owner password>"

# 3. Grant vlms_app exactly what it needs, now that the schema exists.
sudo -u postgres psql -d vlms -f deploy/postgres/10-grant-app-privileges.sql
```

**Verification, not assumption:** confirm `vlms_app` can connect and read/write ordinary tables but
genuinely cannot `UPDATE`/`DELETE` `sensitive_data_access_logs`:

```sh
PGPASSWORD='<vlms_app password>' psql -h <vps-host> -U vlms_app -d vlms \
  -c "UPDATE sensitive_data_access_logs SET user_id = NULL;"
# expected: ERROR: permission denied for table sensitive_data_access_logs
```

If that `UPDATE` succeeds, stop and re-check `deploy/postgres/10-grant-app-privileges.sql` and
`governance/raid.md` D-004 before going any further — this is the one point in this whole
deployment where a mistake silently defeats the audit-log tamper-protection mechanism items 1/2 of
this pivot already built.

## 4. Publish and place `Vlms.Web` / `Vlms.Jobs`

```sh
dotnet publish src/Vlms.Web -c Release -o /opt/vlms/web
dotnet publish src/Vlms.Jobs -c Release -o /opt/vlms/jobs
sudo chown -R vlms:vlms /opt/vlms/web /opt/vlms/jobs
```

Configuration/secrets — one `EnvironmentFile` per unit, mode `0640`, owned by `root:vlms` (readable
by the `vlms` group, never world-readable), never committed anywhere in this repository:

- `/etc/vlms/web.env` — `Vlms.Web`'s real config: `ConnectionStrings__VlmsDatabase` (as `vlms_app`,
  **never** `vlms_owner`), `Smtp__*`, `App__PublicBaseUrl` (the real domain, once one exists — see
  `deploy/Caddyfile`'s own placeholder note; used by `GuardianInviteService` to build the accept-
  invite link), and anything else `src/Vlms.Web/appsettings.json`'s own `_comment` fields flag as
  placeholder-only.
- `/etc/vlms/jobs.env` — see `deploy/systemd/README.md` (already documents this file's shape;
  not repeated here) — same `ConnectionStrings__VlmsDatabase` (as `vlms_app`), `Smtp__*`,
  `Safeguarding__ExpiryWarningWindowDays`.
- `/etc/vlms/backup.env` — see `deploy/backup/pg-backup.sh`'s own comment for the full variable
  list (`PGHOST`/`PGUSER=vlms_app`/`PGPASSWORD`/`BACKUP_S3_*`/`AWS_*`).

## 5. Install the systemd units

```sh
sudo cp deploy/systemd/vlms-web.service /etc/systemd/system/
sudo cp deploy/systemd/vlms-jobs.service deploy/systemd/vlms-jobs.timer /etc/systemd/system/
sudo cp deploy/backup/vlms-backup.service deploy/backup/vlms-backup.timer /etc/systemd/system/
sudo cp deploy/backup/pg-backup.sh /opt/vlms/backup/pg-backup.sh
sudo chown vlms:vlms /opt/vlms/backup/pg-backup.sh
sudo chmod +x /opt/vlms/backup/pg-backup.sh

sudo systemctl daemon-reload
sudo systemctl enable --now vlms-web.service
sudo systemctl enable --now vlms-jobs.timer      # NOT vlms-jobs.service directly — see deploy/systemd/README.md
sudo systemctl enable --now vlms-backup.timer    # NOT vlms-backup.service directly, same reason
```

`vlms-jobs.service`/`.timer` are `deploy/systemd/vlms-jobs.*` — already built and documented in
`deploy/systemd/README.md` (a prior STATE.md item); this step is that README's own "Install"
section, cross-referenced rather than duplicated. `vlms-web.service` and `vlms-backup.*` are this
item's own new units — see their own file comments for the reasoning behind each choice
(`Type=simple` vs `Type=oneshot`, port/`ASPNETCORE_URLS`, the 02:00/03:00 UTC ordering between
backup and the consent/DBS sweep).

## 6. Install and start the Caddyfile

```sh
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile
# Edit /etc/caddy/Caddyfile's PLACEHOLDER-DOMAIN.example to the real domain first.
sudo systemctl reload caddy
```

## 7. Verification checklist

- [ ] `curl -I https://<real-domain>` returns a response with a valid Let's Encrypt certificate
      (or `curl -vI` and inspect the certificate chain) — confirms Caddy's automatic HTTPS actually
      worked, not just that the site block parsed.
- [ ] Sign in via the real domain and confirm a Blazor Server interactive circuit actually connects
      (no SignalR connection errors in the browser console) — confirms Caddy is proxying
      WebSocket upgrades correctly to Kestrel, not just plain HTTP requests.
- [ ] `sudo systemctl status vlms-web.service` shows `active (running)`; `journalctl -u
      vlms-web.service` shows no repeated crash-restart loop.
- [ ] Confirm the app is connecting to PostgreSQL as `vlms_app`, **not** `vlms_owner` or the
      `postgres` superuser: `sudo -u postgres psql -d vlms -c "SELECT usename, count(*) FROM
      pg_stat_activity WHERE datname = 'vlms' GROUP BY usename;"` should show only `vlms_app`
      connections from the application (`vlms_owner` should appear only during a migration run).
- [ ] Re-run the `UPDATE sensitive_data_access_logs` permission-denied check from step 3 once more
      against the live system, not just once at setup time.
- [ ] `sudo systemctl start vlms-jobs.service` (manual one-off run) completes and
      `journalctl -u vlms-jobs.service` shows the expected `ConsentExpiryJob completed: ...` line
      (`deploy/systemd/README.md`'s own verification section covers this in more depth).
- [ ] `sudo systemctl start vlms-backup.service` (manual one-off run) completes, and the resulting
      object actually exists in the off-box bucket (`aws s3 ls s3://<bucket>/postgres/
      --endpoint-url <endpoint>` shows the new file) — confirms the whole chain (`pg_dump` succeeds
      as `vlms_app`, the `aws s3 cp` credentials/endpoint are correct), not just that the script
      ran without a shell error.
- [ ] **Test-restore the backup once, on a scratch database** — `adr/0005`'s own words: "a backup
      that has never been test-restored is not a backup." `pg_restore -d <scratch-db> --clean
      <downloaded-dump-file>` against a throwaway database, then spot-check a few rows.

## 8. What is and isn't verified by this item itself

Consistent with `deploy/systemd/README.md`'s own honesty about what a no-live-VPS environment can
and can't prove: every command-line flag/syntax choice in these files (`pg_dump -Fc`, `aws s3 cp
--endpoint-url`, Caddy's automatic-HTTPS site-block syntax, `ALTER DEFAULT PRIVILEGES FOR ROLE`,
PostgreSQL's default `PUBLIC` privileges on `DATABASE`/`SCHEMA`) was checked against each tool's own
current documentation rather than assumed — citations are inline in the relevant file's own
comments, not repeated here. What is **not** verified, because no real VPS/PostgreSQL
server/object-storage account exists yet: that these files actually parse and run correctly
end-to-end on a live system, that `pg-backup.sh` genuinely produces a restorable dump against a
real bucket, and that Caddy's ACME challenge actually succeeds against a real domain's DNS. Section
7 above is exactly the checklist for proving those once a real VPS exists (`adr/0005`'s "needs
Philip's explicit go-ahead" gate) — this item could not complete that verification itself by
design, the same honestly-reported gap `deploy/systemd/README.md` already carries for its own two
unit files.

## 9. Related, deliberately out of scope here

- **The off-box backup target for local-disk file storage** (certificates, once a page consumes
  `IBlobStorage`/`LocalDiskBlobStorage`) is a separate, not-yet-built concern — `adr/0005` groups it
  with the database backup ("shipped to the same off-box object storage as file backups"), but
  `LocalDiskBlobStorage.cs`'s own doc comment already flags this as open, and `pg-backup.sh` here is
  PostgreSQL-only. Do not assume file backups are covered by anything in this directory.
- **Row-Level Security hardening of `sensitive_data_access_logs`** (`STATE.md` Next item) is a
  separate, higher-scrutiny follow-up to the tamper-protection mechanism this item's PostgreSQL
  scripts rely on (`deploy/postgres/10-grant-app-privileges.sql`'s own header comment cross-
  references it) — not attempted here.
- **OS security patching, PostgreSQL version upgrades, and TLS renewal monitoring** are genuine
  ongoing operational burdens `adr/0005`'s own Consequences section names as owner-carried once a
  real VPS exists — this README covers first-time setup, not ongoing maintenance procedures.
