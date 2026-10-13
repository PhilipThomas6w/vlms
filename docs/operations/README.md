# Operations pack (working markdown)

Delivery-gate source for the operations documents (`docs/` → `/render-gate-package delivery`; gate taxonomy row "Operations: Runbook, Monitoring, DR/BCP, Release, Change/Config").

| File | Renders into | Covers |
|---|---|---|
| `runbook.md` | `VLMS-RUN` | Support model, routine operations, common procedures, incident first steps |
| `host-hardening.md` | `VLMS-RUN` (appendix) | SSH, firewall, unattended security upgrades |
| `monitoring.md` | `VLMS-MON` | What is logged where, journald retention, alerting, uptime checks |
| `dr-bcp.md` | `VLMS-BCP` | Backups, RPO/RTO, restore and rebuild, continuity |
| `release.md` | `VLMS-REL` | Release procedure, migrations, rollback (no staging slot) |
| `configuration.md` | `VLMS-CFG` | Configuration reference, secrets, change control |

## Ground rules

- **The scripts are the source of truth for commands.** `deploy/README.md` is the first-time setup narrative; these documents cover running the system afterwards and point to it rather than copying it.
- **All scheduled times are UTC.** Every timer in `deploy/` uses `OnCalendar=... UTC`, so the jobs run at the same instant all year. In UK local time they fire one hour later during British Summer Time (02:00 UTC = 03:00 BST).
- **Undecided items are marked `[TBC – Philip]`.** Nothing here is a decision until Philip confirms it.
- **Status:** written 13/10/2026 before go-live. No procedure here has yet been run on the production VPS; the go-live item in `STATE.md` is where they are first exercised.
