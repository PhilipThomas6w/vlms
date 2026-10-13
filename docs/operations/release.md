# Release and rollback (VLMS-REL)

There is one environment: production. No staging server or deployment slot exists (`adr/0005`). Releases are made safe by the gate, a pre-release backup, a kept copy of the previous build, and releasing outside session times.

## 1. Before a release

1. `pwsh -File build/verify.ps1` exits 0 on the commit being released (full gate, not `-Fast`).
2. Tag the commit: `git tag vlms-YYYY.MM.DD[-n]` and push the tag. Tag format: `[TBC – Philip]`.
3. Check whether the release adds EF Core migrations (`git diff <last-tag>.. --stat -- src/Vlms.Infrastructure/Migrations`). If it does, read each migration's `Up` and `Down` for anything that drops or rewrites data.
4. Pick a time outside session hours (evenings and weekends are when the system is used; `requirements/non-functional.md`). Release window: `[TBC – Philip]`.

## 2. Build (on the development machine)

Publish framework-dependent for Linux, so the server needs only the ASP.NET Core runtime and never the SDK:

```powershell
dotnet publish src/Vlms.Web  -c Release -r linux-x64 --self-contained false -o artifacts/web
dotnet publish src/Vlms.Jobs -c Release -r linux-x64 --self-contained false -o artifacts/jobs
pwsh -File deploy/build-migrations-bundle.ps1        # only if the release adds migrations; writes artifacts/efbundle
```

Copy to the server's home directory: `scp -r artifacts/web artifacts/jobs [artifacts/efbundle] <admin-user>@<server>:~/release/`.

Note: `deploy/README.md` §4 shows `dotnet publish ... -o /opt/vlms/web` under "On the server", which would need the SDK there. The procedure in this section supersedes it: `[TBC – align deploy/README.md §4]`.

## 3. Deploy (on the server)

```sh
# 1. Fresh backup, so a bad migration can be undone by restore
sudo systemctl start vlms-backup.service && journalctl -u vlms-backup.service -n 5 --no-pager

# 2. Keep the running build for rollback
sudo rm -rf /opt/vlms/web.previous /opt/vlms/jobs.previous
sudo cp -a /opt/vlms/web /opt/vlms/web.previous
sudo cp -a /opt/vlms/jobs /opt/vlms/jobs.previous

# 3. Stop the web app, then migrate (only if the release has migrations)
sudo systemctl stop vlms-web.service
sudo install -o root -g root -m 0700 ~/release/efbundle /opt/vlms-migrations/efbundle
sudo /opt/vlms-migrations/migrate.sh

# 4. Swap in the new build and start
sudo rsync -a --delete ~/release/web/  /opt/vlms/web/
sudo rsync -a --delete ~/release/jobs/ /opt/vlms/jobs/
sudo chown -R vlms:vlms /opt/vlms/web /opt/vlms/jobs
sudo systemctl start vlms-web.service
```

Migrations run while the app is stopped, so the old build never runs against a new schema. Downtime is about a minute. If a unit file in `deploy/` changed, copy it to `/etc/systemd/system/` and run `sudo systemctl daemon-reload` before starting. `vlms-jobs` needs no restart: the next timer run starts the new build.

## 4. Verify

- `systemctl status vlms-web.service` is `active (running)`; no errors in `journalctl -u vlms-web.service -n 100`.
- Sign in, open a page for each role touched by the release, confirm the Blazor circuit connects.
- If the release touched email: trigger a password reset to a test account.
- Record the release (tag, date, migrations applied, result) in `[TBC – Philip: release log location]`.

## 5. Rollback

**Code only (no migrations in the release):**

```sh
sudo systemctl stop vlms-web.service
sudo rsync -a --delete /opt/vlms/web.previous/  /opt/vlms/web/
sudo rsync -a --delete /opt/vlms/jobs.previous/ /opt/vlms/jobs/
sudo systemctl start vlms-web.service
```

**Release included migrations:** the old build may not work against the new schema. Two options:

1. **Restore the pre-release dump** taken in §3 step 1 (`dr-bcp.md` §3 step 4 commands, into the existing database after dropping and recreating it), then the code-only rollback above. Safe and complete, but loses anything users entered since the release. Preferred when the problem is found within minutes.
2. **Down-migrate** with the previous release's bundle and a target migration name. `migrate.sh` does not pass a target migration today, so this means running the bundle by hand with `--connection` as `vlms_owner`. `Down` methods may drop data and are not covered by tests. Use only after reading them.

The rule that settles which: if users have entered data since the release, fix forward with a new release rather than roll back.

## 6. Change control

- Every change reaches production through a git commit that passed the gate. No hotfixes edited on the server.
- Configuration changes follow `configuration.md` §4.
- Approver for production releases: Philip (sole maintainer). No separate change board at this scale.
