# Configuration reference and change management (VLMS-CFG)

Configuration loads in the standard ASP.NET Core order: `appsettings.json` → `appsettings.{Environment}.json` → environment variables. In production the environment variables come from systemd: `Environment=` lines in the unit files (non-secret) and `EnvironmentFile=` files in `/etc/vlms/` (secrets). An environment variable's name is the key with `:` replaced by `__`.

## 1. `Vlms.Web`

| Key | Production value / source | Default (checked in) | Notes |
|---|---|---|---|
| `ConnectionStrings:VlmsDatabase` | `web.env`: `Host=localhost;Database=vlms;Username=vlms_app;Password=…` | local-dev placeholder | **Secret.** Always `vlms_app`, never `vlms_owner` or `postgres` |
| `GraphMail:ClientSecret` | `web.env` | `PLACEHOLDER` | **Secret.** Expires after 2 years; rotation in `deploy/README.md` §4 |
| `GraphMail:TenantId` / `ClientId` / `SenderAddress` | `appsettings.json` | church tenant, mailer app, `noreply@varangians.co.uk` | Identifiers, not secrets |
| `App:PublicBaseUrl` | unit: `https://www.varangians.co.uk` | placeholder | Builds absolute links in invite and account emails |
| `AllowedHosts` | unit: `www.varangians.co.uk` | `*` | Production refuses to start with `*` or empty (`WebHardening`) |
| `DataProtection:KeysPath` | unit: `/var/lib/vlms/keys` | empty | Required in Production; directory created by `StateDirectory=` |
| `Storage:LocalRootPath` | `web.env` `[TBC – Philip: e.g. /var/lib/vlms/blobs or /opt/vlms/blobs]` | `App_Data/blobs` | Must equal `FILES_ROOT` in `backup.env` |
| `Hsts:MaxAgeDays` | default | 365 | |
| `RateLimiting:AccountFormPostsPerMinute` | default | 20 | Per client IP and account page, POSTs only |
| `ASPNETCORE_ENVIRONMENT` | unit: `Production` | n/a | |
| `ASPNETCORE_URLS` | unit: `http://127.0.0.1:5000` | n/a | Must match `reverse_proxy` in the Caddyfile |
| `Logging:LogLevel:*` | default | `Information`; `Microsoft.AspNetCore` `Warning` | |

If `Storage:LocalRootPath` is left at its default, files land under `/opt/vlms/web/App_Data/blobs`, which the release `rsync --delete` (`release.md` §3) would wipe. A production value outside `/opt/vlms/web` is therefore required, not optional.

## 2. `Vlms.Jobs`

| Key | Source | Default | Notes |
|---|---|---|---|
| `ConnectionStrings:VlmsDatabase` | `jobs.env` | placeholder | **Secret.** `vlms_app` |
| `GraphMail:ClientSecret` | `jobs.env` | `PLACEHOLDER` | **Secret.** Same value as `web.env` |
| `GraphMail:*` (others) | `appsettings.json` | as Web | |
| `Safeguarding:ExpiryWarningWindowDays` | `jobs.env` (optional) | 28 | Days before expiry that warnings start |

## 3. Scripts and units

| File | Variables | Notes |
|---|---|---|
| `/etc/vlms/backup.env` | `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER=vlms_app`, `PGPASSWORD`, `BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_DEFAULT_REGION=auto`, `BACKUP_DIR`, `LOCAL_RETENTION_DAYS` (7), `FILES_ROOT` | Full list in `deploy/backup/pg-backup.sh` header |
| `/etc/vlms/migrate.env` | `VLMS_OWNER_CONNECTION` (as `vlms_owner`) | `root:root 0600`; `migrate.sh` refuses any other role |
| Timers | `vlms-backup` 02:00, `vlms-files-backup` 02:30, `vlms-jobs` 03:00 | **UTC**, `Persistent=true` (a missed run fires at next boot) |
| `deploy/Caddyfile` | hostnames, upstream `127.0.0.1:5000`, access log path | |

File permissions: `web.env`, `jobs.env`, `backup.env` are `root:vlms 0640`; `migrate.env` is `root:root 0600`.

## 4. Changing configuration

1. **Checked-in config** (`appsettings.json`, unit files, Caddyfile, scripts): change in git, pass the gate, deploy as a release (`release.md`).
2. **Server-only config** (`/etc/vlms/*.env`): edit with `sudoedit`, then restart the affected unit (`vlms-web.service`; jobs and backups pick it up on their next run). Note the change, without secret values, in `[TBC – Philip: change log location]`.
3. **Never** put a real secret in the repository. The gate's secret scan (gitleaks plus project patterns, `build/check-secrets.ps1`) is the backstop, not the control.
4. **Where secrets are kept off the server:** `[TBC – Philip]` (a password manager). Needed for the rebuild in `dr-bcp.md` §3.

### Secret inventory

| Secret | Used by | Rotation |
|---|---|---|
| `vlms_app` password | web, jobs, backup | on compromise or operator change; `ALTER ROLE`, then update three env files |
| `vlms_owner` password | `migrate.env` | same |
| Graph client secret | web, jobs | every 2 years (expiry) |
| R2 access key pair | backup | on compromise; scope the token to the one bucket |
| DataProtection keys | web | not rotated by hand; the framework rolls keys every 90 days |
| SSH keys | admin access | on device loss |
