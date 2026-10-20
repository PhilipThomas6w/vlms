# `Vlms.Jobs` systemd deployment

Replaces the Azure App Service WebJobs folder convention (`App_Data/jobs/triggered/ConsentExpiryJob/`,
`settings.job`'s NCRONTAB schedule) with a systemd timer/service pair on the self-hosted VPS —
`docs/adr/0003-scheduled-jobs-webjobs.md` (amended by
`docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`). `Vlms.Jobs` itself is unchanged: a
plain `Host.CreateApplicationBuilder` console app that runs `ConsentExpiryJob` + `ConsentExpiryNotifier`
once and exits — only what schedules and invokes it changes.

This directory is scripts/config only. It does not provision a VPS, create the `vlms` system
user/group, or install PostgreSQL/.NET — that's `STATE.md` Next item 1's broader deployment-scripting
item (Caddyfile, systemd unit templates for `Vlms.Web` itself, PostgreSQL setup, backup cron, VPS
setup README). These two unit files are that item's `Vlms.Jobs`-specific piece, built ahead of it
because this STATE.md item was scoped narrowly to the scheduled-jobs retarget.

## Assumed layout

- Dedicated, unprivileged system user/group `vlms` owns everything below (create with
  `useradd --system --no-create-home --shell /usr/sbin/nologin vlms`, or let the broader
  provisioning script from Next item 1 do it — don't run this job as root).
- Published output: `dotnet publish src/Vlms.Jobs -c Release -o /opt/vlms/jobs` (framework-dependent
  — assumes the .NET runtime is already installed on the VPS for `Vlms.Web`'s own Kestrel process;
  no separate runtime dependency).
- Real configuration/secrets: `/etc/vlms/jobs.env`, systemd `EnvironmentFile` format
  (`KEY=VALUE` per line, `__` as the `IConfiguration` section separator), e.g.:
  ```
  ConnectionStrings__VlmsDatabase=Host=localhost;Database=vlms;Username=vlms_app;Password=<real password>
  Safeguarding__ExpiryWarningWindowDays=28
  GraphMail__ClientSecret=<written by the command in deploy/README.md §4 "Email secret">
  App__PublicBaseUrl=https://www.varangians.co.uk
  ```
  `App__PublicBaseUrl` is optional: it adds the parent-dashboard link to consent reminders.
  Mode `0640`, owned by `root:vlms` (readable by the `vlms` group, not world-readable) — never commit
  real values anywhere in this repository; `src/Vlms.Jobs/appsettings.json`'s checked-in values are
  placeholders by design (see its own `_comment` fields).

Adjust the paths in `vlms-jobs.service` (`WorkingDirectory`, `ExecStart`, `EnvironmentFile`) if the
real provisioning script from Next item 1 lands on a different layout — nothing here is load-bearing
beyond "some path the published output actually lives at."

## Install

```sh
sudo cp deploy/systemd/vlms-jobs.service deploy/systemd/vlms-jobs.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now vlms-jobs.timer
```

Do **not** `systemctl enable vlms-jobs.service` directly — only the timer is enabled; the service unit
has no `[Install]` section and is meant to be started exclusively by the timer firing (or manually, for
an ad hoc one-off run, via `systemctl start vlms-jobs.service`).

## Verify

```sh
systemd-analyze calendar '*-*-* 03:00:00 UTC'   # confirms the schedule parses as intended, shows next elapse
systemctl list-timers vlms-jobs.timer           # confirms it's scheduled and shows the next/last run
sudo systemctl start vlms-jobs.service          # run once immediately, on demand, without waiting for 03:00
journalctl -u vlms-jobs.service -n 50           # console logging (Program.cs's ILogger output) lands here
```

A successful run's console output ends with the `ConsentExpiryJob completed: ...` /
`ConsentExpiryNotifier sent ...` `LogInformation` lines from `Program.cs`. A failed run exits non-zero
and logs a `LogCritical` "ConsentExpiryJob run failed." entry — `systemctl status vlms-jobs.service`
shows the failed exit status, and `journalctl -u vlms-jobs.service` has the exception. There is no
Application Insights here (no live Azure environment was ever provisioned for this project); journald
is the monitoring mechanism this ADR names for the systemd host, the same role Application Insights
alerting played for the original Azure WebJob.

## Redeploying a new build

`Vlms.Jobs` is no longer co-located with `Vlms.Web`'s App Service plan, so a `Vlms.Web` deployment no
longer implicitly redeploys this job (see `adr/0003`'s Amendment note) — redeploy it explicitly:

1. `dotnet publish src/Vlms.Jobs -c Release -o /opt/vlms/jobs` (overwrite in place).
2. Nothing else is required for the *next scheduled run* to pick up the new build — systemd starts a
   fresh process from `ExecStart` each time the timer fires; there is no long-running process to
   restart.
3. Only re-run `sudo systemctl daemon-reload` (and re-copy the unit file) if `vlms-jobs.service` or
   `vlms-jobs.timer` *themselves* changed (schedule, paths, environment file location) — a plain
   binary/config update under the same paths needs no systemd-level action at all.

## What is and isn't verified here

This item is deployment configuration, not application logic — there is no live Linux host or CI
runner in this repository to actually install and fire these units against, so nothing here is
exercised end-to-end. What has been checked, and how:

- `Vlms.Jobs`'s own C# code needed no changes for this retarget — confirmed by reading
  `src/Vlms.Jobs/Program.cs` directly (not assumed from `docs/adr/0003`'s own description): it is a
  plain `Host.CreateApplicationBuilder` console app with no WebJobs SDK reference, no code reading any
  `WEBJOBS_*` environment variable, and no dependency on an App Service working-directory/logging
  convention — it reads configuration via the standard `IConfiguration`/`appsettings.json` pipeline and
  logs via the standard `ILogger`, both host-agnostic. `dotnet build -warnaserror` (via
  `build/verify.ps1`) still passes with this directory's `settings.job` removed and the `.csproj`
  reference to it dropped, confirming no build-time dependency existed on that file either.
- The `OnCalendar=*-*-* 03:00:00 UTC` calendar-expression syntax and `Type=oneshot` semantics were
  checked against systemd's own documentation (`systemd.time(7)`, `systemd.service(5)`,
  `systemd.timer(5)`) rather than assumed, per this task's own instruction — see the citations inline
  in `vlms-jobs.timer`/`vlms-jobs.service`'s comments.
- What is **not** verified: that these unit files actually parse and run correctly on a real systemd
  instance (no live VPS exists yet), that the assumed `/opt/vlms`/`/etc/vlms` paths match whatever
  Next item 1's provisioning script actually creates, and that `EnvironmentFile` values actually bind
  onto `IConfiguration` the way `Vlms.Jobs/Program.cs` expects at the real target's configuration
  volume. These become checkable once a real VPS exists (Next item 1, pending Philip's explicit
  go-ahead on provider/region/budget per `adr/0005`'s Consequences) — there is no local way to
  "break and restore" a systemd unit the way this project's other deployment-config items (e.g. the
  ASVS/WCAG gate stages) verified themselves by deliberate break/restore, so this is reported honestly
  as unverified rather than papered over with a hollow test.
