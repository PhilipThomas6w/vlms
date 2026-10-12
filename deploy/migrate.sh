#!/usr/bin/env bash
# migrate.sh — applies the EF Core migrations bundle (built by deploy/build-migrations-bundle.ps1)
# to the vlms database AS vlms_owner, the schema-owning role (deploy/postgres/00-create-roles-and-
# database.sql; governance/raid.md D-004). Never as vlms_app: the runtime role must not be able to
# change the schema, and tables must be owned by vlms_owner for the RLS migration on
# sensitive_data_access_logs to mean anything (docs/adr/0004 §6 pass 8).
#
# Run as root on the VPS: sudo /opt/vlms-migrations/migrate.sh
# Installed under /opt/vlms-migrations (root-owned), deliberately NOT under /opt/vlms: that tree is
# owned by the vlms user, who could then rename the directory and substitute a binary that root runs.
#
# The connection string lives in /etc/vlms/migrate.env (root:root, mode 0600 — only root can read
# the vlms_owner password; no service unit loads this file):
#   VLMS_OWNER_CONNECTION=Host=localhost;Database=vlms;Username=vlms_owner;Password=PLACEHOLDER (the real vlms_owner password)
# Never committed, never embedded in the bundle (Microsoft's bundle guidance:
# learn.microsoft.com/ef/core/managing-schemas/migrations/applying#environment-and-configuration).
#
# The bundle uses EF Core's migration locking and runs each migration in a transaction; re-running
# it when the database is already current is a no-op.
set -euo pipefail

BUNDLE="${BUNDLE:-/opt/vlms-migrations/efbundle}"
ENV_FILE="${ENV_FILE:-/etc/vlms/migrate.env}"

if [[ ! -x "$BUNDLE" ]]; then
  echo "migrate.sh: bundle not found or not executable: $BUNDLE" >&2
  exit 1
fi
if [[ ! -r "$ENV_FILE" ]]; then
  echo "migrate.sh: cannot read $ENV_FILE (run with sudo)" >&2
  exit 1
fi

# Read the value literally rather than `source`-ing the file: an unquoted connection string's `;`
# separators would otherwise be parsed as shell command separators.
VLMS_OWNER_CONNECTION="$(sed -n 's/^VLMS_OWNER_CONNECTION=//p' "$ENV_FILE" | tail -n 1)"
VLMS_OWNER_CONNECTION="${VLMS_OWNER_CONNECTION%\"}"; VLMS_OWNER_CONNECTION="${VLMS_OWNER_CONNECTION#\"}"

if [[ -z "${VLMS_OWNER_CONNECTION:-}" ]]; then
  echo "migrate.sh: VLMS_OWNER_CONNECTION is not set in $ENV_FILE" >&2
  exit 1
fi
shopt -s nocasematch
if [[ ! "$VLMS_OWNER_CONNECTION" =~ (^|;)[[:space:]]*(username|user[[:space:]]?id|user)[[:space:]]*=[[:space:]]*vlms_owner[[:space:]]*(;|$) ]]; then
  echo "migrate.sh: VLMS_OWNER_CONNECTION must connect as vlms_owner (schema owner), not the runtime role" >&2
  exit 1
fi
shopt -u nocasematch

ASPNETCORE_ENVIRONMENT=Production "$BUNDLE" --connection "$VLMS_OWNER_CONNECTION"
