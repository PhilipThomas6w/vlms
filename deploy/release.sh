#!/usr/bin/env bash
# Code-only release of Vlms.Web and Vlms.Jobs (docs/operations/release.md §3). Run on the server as:
#   sudo bash ~/release/release.sh
# after copying artifacts/web and artifacts/jobs to ~/release/ (release.md §2).
#
# Steps: fresh database backup; keep the running build as *.previous for rollback; stop the web app;
# copy the new build in with fixed permissions; start it; show its status.
# Permissions are set explicitly (dirs 0750, files 0640, owner vlms:vlms) because files copied from
# Windows with scp arrive with odd modes (for example drwx---rwx), which rsync -a would otherwise keep.
# The units start the apps with /usr/bin/dotnet <dll>, so nothing needs the execute bit.
#
# Refuses a release that carries a migrations bundle: follow release.md §3 by hand for those.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Run with sudo: sudo bash $0" >&2
    exit 1
fi

src="${SUDO_USER:+$(getent passwd "$SUDO_USER" | cut -d: -f6)}/release"
src="${1:-$src}"

for f in "$src/web/Vlms.Web.dll" "$src/jobs/Vlms.Jobs.dll"; do
    [[ -f "$f" ]] || { echo "Missing $f: copy artifacts/web and artifacts/jobs to $src first." >&2; exit 1; }
done
if [[ -e "$src/efbundle" ]]; then
    echo "$src/efbundle is present: this release has migrations. Follow docs/operations/release.md §3 by hand." >&2
    exit 1
fi

echo "== 1. Database backup"
systemctl start vlms-backup.service
journalctl -u vlms-backup.service -n 5 --no-pager

echo "== 2. Keep the running build for rollback"
rm -rf /opt/vlms/web.previous /opt/vlms/jobs.previous
cp -a /opt/vlms/web /opt/vlms/web.previous
cp -a /opt/vlms/jobs /opt/vlms/jobs.previous

echo "== 3. Swap in the new build"
systemctl stop vlms-web.service
modes='Du=rwx,Dg=rx,Do=,Fu=rw,Fg=r,Fo='
rsync -a --delete --chmod="$modes" "$src/web/"  /opt/vlms/web/
rsync -a --delete --chmod="$modes" "$src/jobs/" /opt/vlms/jobs/
chown -R vlms:vlms /opt/vlms/web /opt/vlms/jobs
systemctl start vlms-web.service

echo "== 4. Check"
sleep 3
systemctl status vlms-web.service --no-pager | head -5
ls -l --time-style=+%F_%T /opt/vlms/web/Vlms.Web.dll
stat -c '%A %U:%G %n' /opt/vlms/web /opt/vlms/web.previous
echo "Done. Rollback if needed: docs/operations/release.md §5."
