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
`deploy/backup/` runs as — nothing VLMS-owned ever runs as `root`.

Because `vlms` has no home directory, the Data Protection key ring (auth cookies, guardian invite
tokens) can't use the framework's default user-profile location. `vlms-web.service` sets
`DataProtection__KeysPath=/var/lib/vlms/keys` and `StateDirectory=vlms/keys` (systemd creates it,
owned by `vlms`, mode `0700`); `Vlms.Web` refuses to start in Production without that setting. Keys
are stored unencrypted at rest, protected by the directory permissions. They are deliberately
**not** shipped off-box with the other backups: anyone holding them could forge sign-in cookies
(including an Admin's, bypassing MFA), whereas losing them only signs everyone out and invalidates
outstanding invite links, which are re-sent. Caddy is installed as its own
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
(`governance/raid.md` D-004, `docs/adr/0004-sensitive-data-access-control.md` §4 pass 6/§6 pass 8)
for why this is two roles and two scripts, not one of each. Summary of the order (getting this order
wrong defeats the least-privilege design — re-read the scripts, don't just copy these commands
blind):

```sh
# 1. Create vlms_owner (migrator/schema-owner role) + vlms_app (runtime role) + the "vlms" database.
sudo -u postgres psql -f deploy/postgres/00-create-roles-and-database.sql
# — then replace the two PLACEHOLDER passwords inside a live cluster via ALTER ROLE ... PASSWORD,
#   not by editing and re-running this file (CREATE ROLE only runs once, guarded by the DO block).

# 2. Apply EF Core migrations AS vlms_owner — this is what actually creates every table, owned by
#    vlms_owner (never vlms_app — that's the whole point), and (via
#    EnforceRowLevelSecurityOnSensitiveDataAccessLog) enables + forces Row-Level Security on
#    sensitive_data_access_logs. Uses the self-contained migrations bundle (build and install it first — §4) — no .NET SDK,
#    dotnet-ef or repository checkout on the server. Create /etc/vlms/migrate.env first (root:root,
#    0600 — see deploy/migrate.sh's header for its one variable, VLMS_OWNER_CONNECTION), then:
sudo /opt/vlms-migrations/migrate.sh

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

**Additional verification, added for the `EnforceRowLevelSecurityOnSensitiveDataAccessLog` migration
(`docs/adr/0004-sensitive-data-access-control.md` §6 pass 8):** the check above proves the
privilege-grant layer (`REVOKE ... FROM PUBLIC`) blocks `vlms_app`. It does not, by itself, prove the
*additional* Row-Level Security layer is active, or that it also binds `vlms_owner` (the table
owner) specifically — the property that made this pass necessary in the first place, since a
`REVOKE` alone never touched the owner. Confirm both, connected as the `postgres` superuser (a
non-superuser cannot see `pg_policies`/`relforcerowsecurity` meaningfully differently, but the
`psql` commands below are simplest run as superuser regardless):

```sh
sudo -u postgres psql -d vlms -c \
  "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'sensitive_data_access_logs';"
# expected: relrowsecurity = t, relforcerowsecurity = t (both true — ENABLE and FORCE are both set)

sudo -u postgres psql -d vlms -c \
  "SELECT policyname, cmd FROM pg_policies WHERE tablename = 'sensitive_data_access_logs';"
# expected: exactly two rows — sensitive_data_access_logs_allow_select (cmd 'r'),
# sensitive_data_access_logs_allow_insert (cmd 'a'). No row for UPDATE/DELETE.

# Then, connected AS vlms_owner specifically (not postgres) — this is the check that proves FORCE is
# actually binding the owner, not just present in the catalog:
PGPASSWORD='<vlms_owner password>' psql -h <vps-host> -U vlms_owner -d vlms \
  -c "UPDATE sensitive_data_access_logs SET user_id = NULL;"
# expected: ERROR: permission denied for table sensitive_data_access_logs (or a row-security-policy
# error) — vlms_owner OWNS this table and would normally bypass RLS entirely; if this UPDATE
# succeeds, FORCE ROW LEVEL SECURITY did not apply and this migration has not achieved what it was
# built for. Stop and re-check the migration before going further.
```

If either check fails (fewer than two policies, `relforcerowsecurity` is `f`, or the `vlms_owner`
`UPDATE` succeeds), the RLS layer has not been applied as designed — re-check that
`EnforceRowLevelSecurityOnSensitiveDataAccessLog` actually ran (`sudo -u postgres psql -d vlms -c
'SELECT migration_id FROM "__EFMigrationsHistory";'`) before assuming step 2 completed correctly.

## 4. Publish and place `Vlms.Web` / `Vlms.Jobs`

Everything is built on the build machine (SDK + `dotnet tool install --global dotnet-ef`); the
server needs only the ASP.NET Core runtime from §2, never the SDK. Publish framework-dependent for
Linux and build the migrations bundle — a self-contained linux-x64 executable that §3 step 2 runs on
the server (same commands as `docs/operations/release.md` §2, which every later release follows):

```powershell
dotnet publish src/Vlms.Web  -c Release -r linux-x64 --self-contained false -o artifacts/web
dotnet publish src/Vlms.Jobs -c Release -r linux-x64 --self-contained false -o artifacts/jobs
pwsh -File deploy/build-migrations-bundle.ps1    # writes artifacts/efbundle (git-ignored)
```

Copy them to the server: `scp -r artifacts/web artifacts/jobs artifacts/efbundle <admin-user>@<server>:~/release/`.
Then, on the server, install the bundle:

```sh
sudo mkdir -p /opt/vlms-migrations
sudo install -o root -g root -m 0700 ~/release/efbundle /opt/vlms-migrations/
sudo install -o root -g root -m 0700 deploy/migrate.sh /opt/vlms-migrations/   # from a checkout or copied alongside
```

The bundle and `migrate.sh` are root-owned and root-only: they run with the `vlms_owner` password,
which the `vlms` runtime user must never see. They live outside `/opt/vlms` because that tree is
owned by `vlms`, which could otherwise replace a binary root later runs. Every release that adds a migration repeats this and
`sudo /opt/vlms-migrations/migrate.sh` **before** restarting `vlms-web.service`; re-running against
a current database is a no-op.

```sh
sudo mkdir -p /opt/vlms/web /opt/vlms/jobs
sudo rsync -a --delete ~/release/web/  /opt/vlms/web/
sudo rsync -a --delete ~/release/jobs/ /opt/vlms/jobs/
sudo chown -R vlms:vlms /opt/vlms/web /opt/vlms/jobs
```

Configuration/secrets — one `EnvironmentFile` per unit, mode `0640`, owned by `root:vlms` (readable
by the `vlms` group, never world-readable), never committed anywhere in this repository:

- `/etc/vlms/web.env` — `Vlms.Web`'s real config: `ConnectionStrings__VlmsDatabase` (as `vlms_app`,
  **never** `vlms_owner`), `GraphMail__ClientSecret` (see "Email secret" below), and anything else
  `src/Vlms.Web/appsettings.json`'s own `_comment` fields flag as placeholder-only.
  (`AllowedHosts` and `App__PublicBaseUrl` are set in `vlms-web.service` itself — not secrets.)
- `/etc/vlms/jobs.env` — see `deploy/systemd/README.md` (already documents this file's shape;
  not repeated here) — same `ConnectionStrings__VlmsDatabase` (as `vlms_app`),
  `GraphMail__ClientSecret`, `Safeguarding__ExpiryWarningWindowDays`.
- `/etc/vlms/backup.env` — see `deploy/backup/pg-backup.sh`'s own comment for the full variable
  list (`PGHOST`/`PGUSER=vlms_app`/`PGPASSWORD`/`BACKUP_S3_*`/`AWS_*`), plus `FILES_ROOT` for
  `deploy/backup/files-backup.sh` — the same path as `Storage__LocalRootPath` in `web.env`.
  `Storage__LocalRootPath` is required in Production: Vlms.Web refuses to start if it is blank,
  relative, or inside `/opt/vlms/web` (`BlobStorageRoot.cs`; `docs/operations/configuration.md`).

### Email secret (Microsoft Graph)

The app sends email through Microsoft Graph as `noreply@varangians.co.uk`, signing in as the Entra
app "Varangians LMS mailer" (`adr/0005` amendment; tenant setup already done and tested). It needs
one secret, `GraphMail__ClientSecret`, in both `web.env` and `jobs.env`. The tenant ID, app ID and
sender address are already in `appsettings.json`.

Create the secret on your own machine (Azure CLI signed in as a tenant admin) and send it straight
into the two files over SSH, so it never appears on screen, in shell history, or in this repo:

```powershell
$secret = az ad app credential reset --id e74439c2-2ada-4204-83b9-d0e686b8675f --append `
  --display-name "vlms-server" --years 2 --query password -o tsv
"GraphMail__ClientSecret=$secret" | ssh <admin-user>@<server> "sudo tee -a /etc/vlms/web.env /etc/vlms/jobs.env > /dev/null"
Remove-Variable secret
```

`--append` adds a secret without deleting existing ones. **The secret expires after 2 years** and
email then stops (sends fail and are logged/escalated by `NotificationService`). Before it expires,
create a new one the same way, remove the old `GraphMail__ClientSecret=` line from both files,
restart `vlms-web.service`, then delete the old secret:
`az ad app credential list --id e74439c2-2ada-4204-83b9-d0e686b8675f` shows each secret's
`keyId` and `endDateTime`; `az ad app credential delete --id e74439c2-2ada-4204-83b9-d0e686b8675f --key-id <old keyId>`.

## 5. Install the systemd units

```sh
sudo cp deploy/systemd/vlms-web.service /etc/systemd/system/
sudo cp deploy/systemd/vlms-jobs.service deploy/systemd/vlms-jobs.timer /etc/systemd/system/
sudo cp deploy/backup/vlms-backup.service deploy/backup/vlms-backup.timer /etc/systemd/system/
sudo cp deploy/backup/vlms-files-backup.service deploy/backup/vlms-files-backup.timer /etc/systemd/system/
sudo cp deploy/backup/pg-backup.sh deploy/backup/files-backup.sh /opt/vlms/backup/
sudo chown vlms:vlms /opt/vlms/backup/pg-backup.sh /opt/vlms/backup/files-backup.sh
sudo chmod +x /opt/vlms/backup/pg-backup.sh /opt/vlms/backup/files-backup.sh

sudo systemctl daemon-reload
sudo systemctl enable --now vlms-web.service
sudo systemctl enable --now vlms-jobs.timer      # NOT vlms-jobs.service directly — see deploy/systemd/README.md
sudo systemctl enable --now vlms-backup.timer    # NOT vlms-backup.service directly, same reason
sudo systemctl enable --now vlms-files-backup.timer
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
# Serves www.varangians.co.uk and redirects varangians.co.uk to it. DNS for both must point at
# this server first (A records in Hostinger), or certificate issuance fails.
sudo systemctl reload caddy
```

## 6a. Bootstrap the first Admin (once per deployment)

A fresh database has no roles at all, so nobody can use the app until an Admin exists. After the
units and Caddyfile are running:

1. Register the operator's own account through the real site's `/Account/Register` page (so the
   password never goes through a shell or its history), then open the confirmation link it emails
   you. Email (`GraphMail__ClientSecret` in `web.env`, §4) must therefore be working first: the bootstrap refuses an
   account whose email isn't confirmed, so a stranger who registered your address first can't be
   the one promoted.
2. Promote that account to Admin with the `Vlms.Jobs` host's one-off `bootstrap-admin` command, run
   as `vlms` with the same environment file the timer uses:

```bash
sudo systemd-run --wait --pipe -p User=vlms -p WorkingDirectory=/opt/vlms/jobs   -p EnvironmentFile=/etc/vlms/jobs.env   /usr/bin/dotnet /opt/vlms/jobs/Vlms.Jobs.dll bootstrap-admin you@example.org
# expected: "First Admin bootstrapped: AppUser <id> (<name>) ..." and exit code 0
```

   It refuses (exit code 1, "An Admin already exists") once any Admin exists, so re-running it later
   is harmless. Every later role change happens in the app at `/administration/user-roles`.
3. Sign in, enrol two-factor authentication (Account → Two-factor authentication). Admin pages stay
   closed until you do.

Not yet verified on a live host (same status as the rest of this file — see §8), including the
`systemd-run -p EnvironmentFile=` form.

## 6b. Seed the rank ladder and curriculum (after migrations; safe to re-run)

The 10 ranks and 56 lessons come from the versioned manifest `seed/curriculum.json`; the lesson
files (Word documents and `Images` folders) are **not** in git, so copy the seed content folder to
the server first (for example to `/tmp/vlms-seed`; it holds the rank folders `Recruits (1)`,
`Guardsman (2)` and so on, as named by the manifest's `source` fields). Delete the copy afterwards:
the `Admin` subfolder holds personal data and is never read.

The command needs the migrated database and the **production** `Storage__LocalRootPath`
(the same directory `Vlms.Web` reads), so `/etc/vlms/jobs.env` must contain
`Storage__LocalRootPath=<the same path as in web.env>` as well as `ConnectionStrings__VlmsDatabase`.
Run it as `vlms` with the same environment file the timer uses:

```bash
sudo systemd-run --wait --pipe -p User=vlms -p WorkingDirectory=/opt/vlms/jobs   -p EnvironmentFile=/etc/vlms/jobs.env   /usr/bin/dotnet /opt/vlms/jobs/Vlms.Jobs.dll seed-curriculum --manifest /opt/vlms/seed/curriculum.json --content-root /tmp/vlms-seed
# expected: "seed-curriculum done: ranks 10 created ...; lessons 56 created ...; 314 file(s) uploaded." and exit code 0
```

(`seed/curriculum.json` is not part of the published output: copy it from the repository alongside
the content, and point `--manifest` at wherever you put it.)

- It validates everything first (every lesson folder exists, every tag is a known tag, codes are
  unique). On any problem it writes nothing, prints the full list of errors and exits non-zero (1;
  2 for bad arguments).
- Re-running with the same input changes nothing. After editing the manifest or a document, re-run
  it to apply the change. Files are uploaded under `lessons/<lesson code>/...` and overwritten.
- It never deletes: a lesson or rank that is in the database but no longer in the manifest is
  reported as a warning and left alone.

Not yet verified on a live host or against PostgreSQL (tests use SQLite in memory), including the
`systemd-run -p EnvironmentFile=` form.

## 7. Verification checklist

- [ ] `curl -I https://<real-domain>` returns a response with a valid Let's Encrypt certificate
      (or `curl -vI` and inspect the certificate chain) — confirms Caddy's automatic HTTPS actually
      worked, not just that the site block parsed.
- [ ] Sign in via the real domain and confirm a Blazor Server interactive circuit actually connects
      (no SignalR connection errors in the browser console) — confirms Caddy is proxying
      WebSocket upgrades correctly to Kestrel, not just plain HTTP requests.
- [ ] `sudo systemctl status vlms-web.service` shows `active (running)`; `journalctl -u
      vlms-web.service` shows no repeated crash-restart loop.
- [ ] Email sends: register a test account (or use "Forgot your password?") and check the email
      arrives from `noreply@varangians.co.uk`; in the received message's headers, `dkim=pass` and
      `spf=pass` for `varangians.co.uk`. Exchange can take 30 minutes to 2 hours to apply app
      permission changes, so a first send right after tenant changes may fail and retry.
- [ ] Data Protection keys persist: sign in, `sudo systemctl restart vlms-web.service`, reload —
      still signed in; `sudo ls -ld /var/lib/vlms/keys` shows `drwx------ vlms vlms` with a
      `key-*.xml` inside.
- [ ] Confirm the app is connecting to PostgreSQL as `vlms_app`, **not** `vlms_owner` or the
      `postgres` superuser: `sudo -u postgres psql -d vlms -c "SELECT usename, count(*) FROM
      pg_stat_activity WHERE datname = 'vlms' GROUP BY usename;"` should show only `vlms_app`
      connections from the application (`vlms_owner` should appear only during a migration run).
- [ ] Re-run the `UPDATE sensitive_data_access_logs` permission-denied check from step 3 once more
      against the live system, not just once at setup time — including the `vlms_owner` variant
      (step 3's "additional verification" for the RLS migration), since that is the check that
      proves the table owner is genuinely bound by `FORCE ROW LEVEL SECURITY` and not just exempt
      the way it would be under `ENABLE` alone.
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
- [ ] `sudo systemctl start vlms-files-backup.service` completes and `aws s3 ls
      s3://<bucket>/files/ --recursive --endpoint-url <endpoint>` lists the certificate PDFs —
      confirms `FILES_ROOT` matches `Storage__LocalRootPath` and the sync reached the bucket.
- [ ] Turn on object versioning (or the provider's equivalent) for the bucket, so an overwritten
      object can be recovered — the sync never deletes, but it does overwrite a changed file.

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

## 9. Restoring file storage

`deploy/backup/files-backup.sh` syncs `FILES_ROOT` (the certificate PDFs written by
`LocalDiskBlobStorage`) to `s3://<bucket>/files/` nightly at 02:30 UTC, never deleting from the
bucket. To restore onto a rebuilt server (after §1–§5, before starting `vlms-web.service`):

```sh
sudo systemctl stop vlms-web.service
sudo systemd-run --wait --pipe -p User=vlms -p Group=vlms -p EnvironmentFile=/etc/vlms/backup.env /opt/vlms/backup/files-backup.sh --restore
sudo systemctl start vlms-web.service
```

`systemd-run` reuses `backup.env` exactly as the timer's unit reads it and runs as `vlms`, so
restored files come back owned by `vlms`.

Then restore the database (`pg_restore`, §7) from the dump taken the same night, so certificate
records and their PDFs match. The restore drill itself is the separate "Backup restore test" item
in `STATE.md`.

## 10. Related, deliberately out of scope here

- **Row-Level Security hardening of `sensitive_data_access_logs`** (`STATE.md` Next item) is a
  separate, higher-scrutiny follow-up to the tamper-protection mechanism this item's PostgreSQL
  scripts rely on (`deploy/postgres/10-grant-app-privileges.sql`'s own header comment cross-
  references it) — not attempted here.
- **OS security patching, PostgreSQL version upgrades, and TLS renewal monitoring** are genuine
  ongoing operational burdens `adr/0005`'s own Consequences section names as owner-carried once a
  real VPS exists — this README covers first-time setup, not ongoing maintenance procedures.
  Those live in the operations pack, `docs/operations/` (runbook, host hardening, monitoring,
  DR/BCP, release and rollback, configuration reference).
