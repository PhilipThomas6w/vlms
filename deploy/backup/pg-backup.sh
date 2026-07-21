#!/usr/bin/env bash
set -euo pipefail

# pg-backup.sh — nightly PostgreSQL logical backup for VLMS, shipped off-box to S3-compatible
# object storage. docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md: "Database backup:
# pg_dump on a cron schedule, shipped to the same off-box object storage as file backups." STATE.md
# hosting-pivot item 6. Invoked by vlms-backup.timer/vlms-backup.service in this directory — not
# run directly by cron, matching this deployment's systemd-first convention (deploy/systemd/).
#
# *** Off-box object storage provider is still an OPEN, undecided choice (Cloudflare R2 vs
# Backblaze B2 — adr/0005 Decision table) — this script targets the S3-compatible API both
# providers implement, with placeholder endpoint/bucket/credential configuration, following this
# project's own established "structurally correct, not yet wired to a live target" pattern (see
# src/Vlms.Infrastructure/Storage/LocalDiskBlobStorage.cs's own doc comment for the identical gap on
# the file-storage side of the same ADR decision — that class's own off-box backup is a separate,
# still-not-built concern this script does NOT cover; this script is PostgreSQL only). Do not treat
# the config below as real values — every one is a PLACEHOLDER. ***
#
# Configuration — systemd EnvironmentFile, /etc/vlms/backup.env (never committed; mode 0640,
# root:vlms, same convention as jobs.env/web.env — see deploy/systemd/README.md):
#   PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD
#       Standard libpq environment variables (postgresql.org/docs/current/libpq-envars.html,
#       verified rather than assumed) — pg_dump below takes no -h/-U/-d flags at all and relies on
#       these being set, deliberately, to avoid a second place connection details could drift from
#       the app's own ConnectionStrings:VlmsDatabase. PGUSER=vlms_app — the SAME least-privilege
#       runtime role Vlms.Web/Vlms.Jobs connect as (deploy/postgres/10-grant-app-privileges.sql):
#       it holds SELECT on every table, including sensitive_data_access_logs (SELECT/INSERT only,
#       governance/raid.md D-004), which is exactly and only what a read-only backup needs — no
#       third, more-privileged credential is introduced solely for backups. PGPASSWORD is used here
#       (rather than a .pgpass file, which PostgreSQL's own docs prefer) to stay consistent with
#       this repository's existing EnvironmentFile-based secrets convention, a deliberate choice,
#       not an oversight.
#   BACKUP_S3_ENDPOINT   PLACEHOLDER — e.g. https://<account-id>.r2.cloudflarestorage.com
#                        (Cloudflare R2) or Backblaze B2's S3-compatible endpoint for your region,
#                        once the provider decision above is actually made.
#   BACKUP_S3_BUCKET     PLACEHOLDER bucket name.
#   AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY
#                        The S3-compatible credential pair (both R2 and B2 issue these in their own
#                        dashboards) — read automatically by the aws CLI, no corresponding flag
#                        needed.
#   AWS_DEFAULT_REGION   "auto" for Cloudflare R2 — verified against Cloudflare's own aws-cli guide
#                        (developers.cloudflare.com/r2/examples/aws/aws-cli/): "auto" is required by
#                        the SDK but unused by R2 itself. Set to the real region string instead if
#                        Backblaze B2 is chosen.
#   BACKUP_DIR           Local staging directory for dump files before/after upload. Default below.
#   LOCAL_RETENTION_DAYS How many days of LOCAL dump files to keep (defense in depth if an upload
#                        fails silently) — default below. Off-box retention/lifecycle is NOT
#                        managed by this script (see note near the bottom).
#
# aws CLI syntax verified against docs.aws.amazon.com/cli/latest/reference/s3/cp.html and
# developers.cloudflare.com/r2/examples/aws/aws-cli/ rather than assumed — `aws s3 cp <file>
# s3://<bucket>/<key> --endpoint-url <url>` is the exact, currently-documented form for uploading to
# an S3-compatible endpoint that isn't AWS's own.
#
# pg_dump -Fc: custom format, compressed by default, restorable selectively via pg_restore
# (postgresql.org/docs/current/app-pgdump.html) — verified rather than assumed; a plain-SQL dump
# was deliberately not used, since pg_restore's ability to be selective about what's restored is
# worth the (small) extra step of needing pg_restore rather than psql to restore from it. To
# restore: `pg_restore -d <target-database> --clean <dumpfile>` (same page).

BACKUP_DIR="${BACKUP_DIR:-/var/backups/vlms}"
LOCAL_RETENTION_DAYS="${LOCAL_RETENTION_DAYS:-7}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DUMP_FILE="${BACKUP_DIR}/vlms-${TIMESTAMP}.dump"

for required_var in BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET; do
    if [ -z "${!required_var:-}" ]; then
        echo "pg-backup.sh: ${required_var} is not set — refusing to run with placeholder/missing config" >&2
        exit 1
    fi
done

mkdir -p "$BACKUP_DIR"

echo "pg-backup.sh: starting pg_dump (PGDATABASE=${PGDATABASE:-<unset, relies on libpq default>}) to ${DUMP_FILE}"
pg_dump -Fc -f "$DUMP_FILE"

echo "pg-backup.sh: dump written ($(du -h "$DUMP_FILE" | cut -f1)) — uploading to s3://${BACKUP_S3_BUCKET}/postgres/$(basename "$DUMP_FILE")"
aws s3 cp "$DUMP_FILE" "s3://${BACKUP_S3_BUCKET}/postgres/$(basename "$DUMP_FILE")" \
    --endpoint-url "$BACKUP_S3_ENDPOINT"

echo "pg-backup.sh: upload confirmed — pruning local dumps older than ${LOCAL_RETENTION_DAYS} day(s) from ${BACKUP_DIR}"
find "$BACKUP_DIR" -maxdepth 1 -name 'vlms-*.dump' -mtime "+${LOCAL_RETENTION_DAYS}" -print -delete

echo "pg-backup.sh: done"

# Off-box (remote) retention/lifecycle is deliberately NOT managed by this script — deleting old
# backups from the wrong place is a worse failure mode than a bucket slowly growing, and both R2
# and B2 support native bucket lifecycle rules (expire objects after N days) that are the correct,
# provider-native way to do this once the provider is actually chosen; scripting ad hoc `aws s3 rm`
# deletion loops against a bucket that is this system's only off-box copy of safeguarding data was
# judged not worth the risk for what a lifecycle policy already does natively.
#
# *** NOT VERIFIED: this script has never been run against a real PostgreSQL server or a real
# S3-compatible bucket — no live VPS or object-storage account exists yet (adr/0005 Consequences:
# provisioning needs Philip's explicit go-ahead, separately from this being ready to run once given
# one). What IS verified: the pg_dump/aws CLI flag syntax against each tool's own current
# documentation (cited above), and that PGUSER=vlms_app's grants (deploy/postgres/10-grant-app-privileges.sql)
# are sufficient for a full logical dump (SELECT on every table). "A backup that has never been
# test-restored is not a backup" (adr/0005's own words) — the first real run of this script, and a
# real `pg_restore` test-restore of its output, are both operational verification steps for whoever
# stands up the real VPS, not something this scripted-only item can complete itself. ***
