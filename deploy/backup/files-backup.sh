#!/usr/bin/env bash
set -euo pipefail

# files-backup.sh — nightly off-box copy of LocalDiskBlobStorage's files (certificate PDFs) to the
# same S3-compatible bucket pg-backup.sh uses, under the files/ prefix (adr/0005: "pg_dump ...
# shipped to the same off-box object storage as file backups"). Invoked by
# vlms-files-backup.timer/.service in this directory.
#
# Configuration — the same /etc/vlms/backup.env as pg-backup.sh (BACKUP_S3_ENDPOINT,
# BACKUP_S3_BUCKET, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_DEFAULT_REGION — see that script's
# header), plus:
#   FILES_ROOT  The directory Vlms.Web writes blobs to — must equal Storage__LocalRootPath in
#               /etc/vlms/web.env (or, if that is unset, /opt/vlms/web/App_Data/blobs:
#               LocalDiskBlobStorage.DefaultRootPath relative to vlms-web.service's
#               WorkingDirectory). Required: no default here, so a mismatch fails loudly instead of
#               silently backing up an empty directory.
#
# `aws s3 sync <dir> s3://<bucket>/files/ --endpoint-url <url>`
# (docs.aws.amazon.com/cli/latest/reference/s3/sync.html) uploads only new or changed files.
# Deliberately WITHOUT --delete: a file removed or wiped locally is never removed from the bucket,
# so local loss or corruption can't propagate off-box. Blob keys are write-once
# (certificates/{studentId}/{completionId}.pdf), so this doesn't accumulate stale versions. The
# retention purge (STATE.md, not yet built) will need to delete purged students' files from the
# bucket explicitly — this script never will.
#
# Restore: `files-backup.sh --restore` syncs the bucket back into FILES_ROOT (creating it). Run it
# as vlms with the same environment file, so files come back owned by vlms (deploy/README.md §9).
#
# Not run against a live bucket yet (no VPS/storage account exists) — same status as pg-backup.sh.

for required_var in BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET FILES_ROOT; do
    if [ -z "${!required_var:-}" ]; then
        echo "files-backup.sh: ${required_var} is not set — refusing to run with placeholder/missing config" >&2
        exit 1
    fi
done

if [ "${1:-}" = "--restore" ]; then
    mkdir -p "$FILES_ROOT"
    echo "files-backup.sh: restoring s3://${BACKUP_S3_BUCKET}/files/ into ${FILES_ROOT}"
    aws s3 sync "s3://${BACKUP_S3_BUCKET}/files/" "$FILES_ROOT" --endpoint-url "$BACKUP_S3_ENDPOINT"
    echo "files-backup.sh: restore done"
    exit 0
fi

if [ ! -d "$FILES_ROOT" ]; then
    echo "files-backup.sh: FILES_ROOT ${FILES_ROOT} does not exist — check it matches Storage__LocalRootPath" >&2
    exit 1
fi

echo "files-backup.sh: syncing ${FILES_ROOT} to s3://${BACKUP_S3_BUCKET}/files/"
aws s3 sync "$FILES_ROOT" "s3://${BACKUP_S3_BUCKET}/files/" --endpoint-url "$BACKUP_S3_ENDPOINT"

echo "files-backup.sh: done"
