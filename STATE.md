## Goal
Build the VLMS MVP per `docs/VISION.md`'s current-increment acceptance criteria: curriculum management, progress tracking, safeguarding & consent (with the corrected access-control model), parent engagement, and reporting.

Full narrative for Done/Log items to 12/08/2026: `docs/harness/STATE-archive.md`. Hashes below are current `git log` hashes; a few older hashes in the archive pre-date a history rewrite and do not resolve.

## Done
- [x] Solution scaffolded (`Vlms.Domain/Infrastructure/Web/Tests`) and `build/verify.ps1` wired to real build/test/secrets stages, green — 59d96df
- [x] EF Core data model: 16 entities plus ADR-0004 whole-entity sensitive-data access control and audit logging — 97e61d9
- [x] Entra External ID sign-in, `AppUser`/`UserRole` provisioning, role + resource-based authorization — c318bc5 (superseded by ADR-0005: self-rolled ASP.NET Core Identity with TOTP MFA)
- [x] Curriculum management (`LessonProposalService` + Teacher/Approver pages), incl. interactive-render auth fix and checker-FAIL sign-in regression fix 1197b8c, cef5c16 — 863c5cb
- [x] Progress tracking: `CompletionService`, `PromotionService`, `CertificateService` (service layer only) — 9a1c898
- [x] Guardian-link creation flow, Admin/Teacher only (FR-004) — 2867a60
- [x] Student registration/enrolment flow, incl. checker-FAIL atomicity fix d3a7e0d — b85cf38
- [x] Safeguarding & consent UI and `ConsentExpiryJob` WebJob, incl. checker-FAIL DBS audit-gap fix d23f6e4 — 8c458a5 (superseded by ADR-0005: systemd timer replaces WebJob host)
- [x] Parent dashboard + `NotificationService` with retry/escalation for safeguarding-critical mail — 1df0142 (superseded by ADR-0005: SMTP/MailKit replaces Azure Communication Services Email)
- [x] Reporting screens: rank/completion stats, promotion history, at-risk flagging — f3ebc26
- [x] PWA manifest/service worker for installability — 22ea54d
- [x] `verify.ps1` full-only stages: ASVS 5.0 access-control and WCAG 2.2 AA checks, incl. checker-FAIL checklist-hash portability fix dff3456 — ca14c0a
- [x] ADR-0004 §4 tamper protection: `DENY UPDATE/DELETE` on `SensitiveDataAccessLogs` — a693916 (superseded by ADR-0005: PostgreSQL REVOKE + Row-Level Security, 588cb45)
- [x] Chose `DENY ... TO public` over dedicated `VlmsAppRole` for the tamper protection — 7625588 (superseded by ADR-0005: PostgreSQL RLS replaces SQL Server DENY)
- [x] Secrets-scan stage tightened for Azure/SQL/Entra credential shapes, incl. checker-FAIL fix for three holes a78cc18 — 59a069a
- [x] Full `openwiki/` refresh (`loop-harness:doc-refresh`) across every page — 22a0b7d
- [x] Certificate hidden from parent dashboard when its completion is reversed (Philip's decision 20/07/2026) — 925f7ab
- [x] EF Core provider migration, Azure SQL to self-hosted PostgreSQL (Npgsql); ADR-0005 hosting pivot item 1 — 199fa40 (ADR-0005 recorded in f72093b)
- [x] Identity: Entra External ID replaced by ASP.NET Core Identity with TOTP MFA and signed invite links, incl. checker-FAIL fix 6f33fd4 — fa2be61
- [x] File storage: `AzureBlobStorage` replaced by `LocalDiskBlobStorage`; ADR-0005 item 3 — 8e1299c
- [x] Email: `AzureCommunicationEmailSender` replaced by `SmtpEmailSender` (MailKit); ADR-0005 item 5 — f8374f5
- [x] Scheduled jobs: `Vlms.Jobs` retargeted from WebJobs folder convention to a systemd timer; ADR-0005 item 6 — 8087bcf
- [x] Deployment scripting: Caddyfile, `vlms-web.service`, PostgreSQL role/grant scripts, `pg_dump` backup, `deploy/README.md`, incl. checker-FAIL `pg-backup.sh` grant fix cdd9e36 — eb51a59
- [x] `build/verify.ps1`/`check-secrets.ps1` currency audit against the pivoted stack; last ADR-0005 core item — 239829f
- [x] `sensitive_data_access_logs` tamper protection hardened with PostgreSQL Row-Level Security (ENABLE + FORCE) — 588cb45
- [x] Teacher-facing "reverse a completion" flow (`CompletionService.ReverseAsync`) — f9edca7
- [x] "Records due for deletion" retention report (`RetentionFlagging`, Admin page, widened `ConsentExpiryJob` sweep) — 73eb0f8
- [x] WCAG 4.1.3 status messages: `role="status"` confirmations on the list-then-form pages — def8f75
- [x] WCAG 1.1.1: `aria-label` on `MainLayout` dismiss glyph — 81ad565
- [x] Manual "leaving" flag for students and teachers feeding the retention report (Philip's decision 12/08/2026) — c1af6e9
- [x] `docs/design/` currency pass (Azure/Entra to self-hosted, ADR-0005 cross-references) — 218f545
- [x] WCAG 2.1.1/2.4.3 keyboard-operability checklist review, incl. checker-FAIL fix (`MainLayout` dismiss as real button) 020cc37 — 21b4a9c
- [x] WCAG 3.3.1/3.3.3 error-identification review: `role="alert"` on `ValidationSummary` and framework error surfaces — f14b9a3
- [x] Three remaining stale Azure/Entra references fixed outside `docs/design/` — 42d1575
- [x] Admin role assignment (`/administration/user-roles`, last-Admin and leaver guards) + first-Admin bootstrap (`Vlms.Jobs bootstrap-admin`, inert once an Admin exists) — 16ba130; full gate exit 0, 340/340 tests
- [x] Waiting-for-approval list on `/administration/user-roles` (role-less non-leavers, invite origin, linked students; Approve as Parent for invite accounts only) — 59797a6; full gate exit 0, 350/350 tests
- [x] Persist DataProtection keys (`VlmsDataProtection`: fixed app name, `DataProtection:KeysPath` required in Production; `StateDirectory=vlms/keys` 0700 in `vlms-web.service`; README §1/§7) — 037d6d9; full gate exit 0, 355/355 tests. On-host restart check still pending (README §7).
- [x] EF migration bundle + deploy script (`deploy/build-migrations-bundle.ps1` self-contained linux-x64; `deploy/migrate.sh` as `vlms_owner` from `/etc/vlms/migrate.env`; README §3 no longer needs the SDK) — 2b0b8a5; full gate exit 0, 360/360 tests. Bundle build verified locally; on-host run pending go-live.
- [x] Off-box backup of file storage (`deploy/backup/files-backup.sh` sync to `files/`, no `--delete`, `--restore` mode; `vlms-files-backup.service/.timer` 02:30 UTC; README §9 restore steps; DataProtection keys deliberately excluded) — dac8d81; full gate exit 0, 366/366 tests. Live-bucket run pending go-live (Cloudflare R2 decided).
- [x] Password reset + Identity hardening (`AccountEmailService` reset/confirm by email, no enumeration; `VlmsIdentityPolicy`: confirmed email required, lockout 5 / 15 min, 2-hour links; 4 account pages; invite accounts pre-confirmed; bootstrap refuses unconfirmed) — 8326bd3; full gate exit 0, 384/384 tests. Pages not exercised against a live DB (none locally); first Admin now needs working email (Graph secret, `deploy/README.md` §4).
- [x] Production web hardening (`WebHardening`: Production requires explicit `AllowedHosts`; HSTS 365 days; account-form POSTs rate-limited per IP and page, default 20/min; unit/Caddyfile name `www.varangians.co.uk`, bare domain redirects) — 2b8a4bd; full gate exit 0, 404/404 tests.
- [x] Send email via Microsoft Graph (`GraphEmailSender`, app-only token, scoped to `noreply@varangians.co.uk`; MailKit/SMTP removed; secret via env only, creation/rotation in `deploy/README.md` §4; secret scan regains Entra client-secret patterns) — c56e9dc; full gate exit 0, 409/409 tests. First real send pending go-live (secret not yet created).
- [x] Jobs appsettings stale comment: Azure App Service/WebJob wording removed from `src/Vlms.Jobs` (appsettings `_comment`s, `Program.cs`, `.csproj`); production config source now named as `/etc/vlms/jobs.env` — abc366f; full gate exit 0, 409/409 tests; `grep -i azure src/Vlms.Jobs` (excl. bin/obj) empty.
- [x] Real secret scanning: gitleaks 8.30.1 installed (winget `Gitleaks.Gitleaks`); `check-secrets.ps1` runs `gitleaks dir` first, then the project regex set as a supplement (gitleaks misses JSON `"Password"` and PostgreSQL `PASSWORD '...'`), regex alone when gitleaks absent — d02bb34; full gate exit 0, 409/409 tests, output "secret scan (gitleaks): clean; project patterns: clean"; planted Entra secret and JSON password both fail the stage.
- [x] Ops pack markdown sources: `docs/operations/` (README, runbook, host-hardening, monitoring, dr-bcp, release, configuration), timers stated as UTC, undecided items `[TBC – Philip]`; linked from `deploy/README.md` §10 — 8c9d9e4; full gate exit 0. Found: `Storage__LocalRootPath` must be set outside `/opt/vlms/web` in production or a release `rsync --delete` wipes certificate PDFs; `deploy/README.md` §4 implies SDK on server (superseded by `release.md` §2).
- [x] Delivery-gate evidence docs: TC-001..TC-014 results table (`quality/test-plan.md` Results, automated pass, manual/production halves pending), `quality/uat-report.md` template (UAT-01..15), draft `governance/dpia.md` and `governance/privacy-notice.md` for Philip, RTM completed (`quality/traceability.md`); new test `Approver_CannotRead_DbsCheck_Or_ConsentSensitiveDetails_ButCanRead_ConsentRecord` (fails when filter admits Approver) — 2711a71; full gate exit 0, 410/410 tests.
- [x] Delivery gate package rendered: 19 `.docx` in `gates/delivery/` (CHTR, BRD, SRS, NFR, SDA, LLD, ADR, RAID, GLOS, DEL, TEST, RTM, SEC with DPIA/privacy notice/ASVS annexes, UAT template, RUN, MON, BCP, REL, CFG) via `/render-gate-package delivery`, `[TBC]` gaps listed per document — 4b52355; full gate exit 0, 410/410 tests. Not opened in Word; TOC needs Update Field. Re-render after Philip's decisions and UAT.

- [x] Production storage-root guard (`BlobStorageRoot`: Production refuses blank, relative, or content-root/base-directory `Storage:LocalRootPath`; resolved once at startup; README §4, `vlms-web.service`, `configuration.md` updated; ASVS re-review row awaiting countersign) — 3e426f1; full gate exit 0, 421/421 tests. Path itself still Philip's (Decisions 5).
- [x] Proactive parent notifications, BR-005 (`GuardianNotificationService`: completion/promotion emails from `CompletionService` after all saves, never throwing; consent reminders from `Vlms.Jobs` at 28/7/1 days before expiry plus a safeguarding-critical notice the day after lapse, no stored reminder state; recipients only linked, `Parent`-role, not-left guardians; no restricted data read; TC-015, RTM BR-005 covered, LLD updated; ASVS re-review row awaiting countersign) — 180ff05; full gate exit 0, 438/438 tests. First live send pending go-live.
- [x] Stale deploy wording: `deploy/README.md` §4 publishes on the build machine and rsyncs to the server (no SDK on the server, matches `release.md` §2; its `[TBC – align]` note removed); `vlms-jobs.service`/`vlms-web.service` comments name the Graph secret, not SMTP — ee0cb13; full gate exit 0, 438/438 tests; `grep -ri smtp deploy/` empty.
## In progress
_(none)_

## Next
1. **Align Microsoft 10.0.x packages to 10.0.13** — only `System.Security.Cryptography.Xml`/`Pkcs` moved (f81df92); Identity, DataProtection, Logging and EF Core packages remain 10.0.10, and `Authorization`, `Components.Authorization`, `EntityFrameworkCore.Design` 10.0.0. Acceptance: all `Microsoft.*` 10.0.x references on one servicing version, `dotnet list package --vulnerable` clean, gate exits 0.

## Decisions needed (Philip)
Ordered by go-live critical path.
1. **Seed rank ladder + curriculum** — confirm the source rows (rank names/order, lesson list per rank). Unblocks: seed item.
2. **Retention purge approach** — manually run `BYPASSRLS` maintenance role vs scheduled job (adr/0004 pass 8); only flagging exists (`RecordsDueForDeletionService.cs:30`). `vlms_app` must never get DELETE on `sensitive_data_access_logs`. DPIA residual risk 7. Unblocks: purge item.
3. **Alert route** (ops pack) — DPIA residual risk 8; needed before go-live.
4. **DPIA and privacy notice** — controller, lawful bases/Art 9, DBS (Schedule 1) conditions, processor locations, sign-off (`governance/dpia.md`, `governance/privacy-notice.md`); and whether ICO registration applies (to verify, not asserted here).
5. **Ops pack `[TBC – Philip]` markers** in `docs/operations/` — storage path (`Storage:LocalRootPath`, outside `/opt/vlms/web`), uptime monitor, journald retention, auto-reboot, SSH source restriction, R2 lifecycle period, RPO/RTO acceptance, break-glass, secrets store, log locations.
6. **Palette and icon direction** — unblocks branding; WCAG 1.4.3/1.4.11 contrast is still open, so this is not cosmetic only.
7. **Countersign** the ASVS and WCAG checklist rows signed by Claude since 2026-10-11 (ASVS BR-005 row countersigned 20/10/2026; the other ASVS rows from 2026-10-11 onwards and the WCAG rows still pending) (`docs/governance/asvs-access-control-checklist.md`, `docs/quality/wcag-2.2-aa-checklist.md`).
8. **Stack wording in project law** — `docs/VISION.md:7` and `CLAUDE.md:15` still say "SMTP email"; Graph `sendMail` was decided 12/10/2026 (adr/0005 amendment). Confirm the edit to "Microsoft Graph email".
9. **Go-ahead for hosting accounts** — all choices decided (DigitalOcean LON1 Basic 2 GiB/1 vCPU/50 GiB, $12/month; www.varangians.co.uk; backups to Cloudflare R2; Philip 12/10/2026). ADR-0005 requires explicit go-ahead before any account is created or anything provisioned.

## Waiting on a decision or go-live
- **Seed rank ladder + curriculum** (needs decision 1) — idempotent seed of the confirmed rows. Acceptance: re-runnable seed, tests, gate exits 0.
- **Retention purge** (needs decision 2) — hard delete at 3y students / 6y DBS + audit log, by the chosen mechanism. Acceptance: purge implemented and tested, DPIA risk 7 updated, gate exits 0.
- **Branding + WCAG colour contrast** (needs decision 6) — VLMS palette and icon replacing the template `src/Vlms.Web/wwwroot/app.css` and placeholder `wwwroot/icons/icon.svg`, then close "Colour contrast (1.4.3/1.4.11)" in `docs/quality/wcag-2.2-aa-checklist.md`. Acceptance: branding landed, contrast verified at 4.5:1 text / 3:1 UI, checklist item closed, gate exits 0.
- **VPS go-live** (needs decision 9, the Next items, and seed/purge) — operator (Philip) work with a human-in-the-loop component. Create DigitalOcean and Cloudflare R2 accounts; A records for `varangians.co.uk` and `www` in Hostinger; create the Graph email secret (`deploy/README.md` §4 "Email secret"); harden SSH/firewall/unattended-upgrades per the ops pack; provision and deploy per `deploy/README.md` (§1 OS prep, §2 PostgreSQL/Caddy/.NET, §3 roles and grants, §4 publish, §5 systemd units, §6 Caddyfile, §7 verification). Go-live verification checklist (carried from Done items):
  - DataProtection keys survive a service restart (README §7; 037d6d9)
  - migration bundle runs on the host via `deploy/migrate.sh` (2b0b8a5)
  - first live R2 run of `pg-backup.sh` and `files-backup.sh` (dac8d81)
  - first real Graph send from `noreply@varangians.co.uk` (c56e9dc)
  - password reset, email confirmation and lockout exercised against the live DB (8326bd3)
  - TLS via Caddy; bare domain redirects to `www` (2b8a4bd)
  - smoke test of each role's core journey
  Acceptance: app reachable on the production hostname; checklist and smoke test recorded.
- **Backup restore test** (needs go-live) — restore a `pg_dump` backup (`deploy/backup/pg-backup.sh`) from R2 into a scratch database (`deploy/README.md` §7, last bullet), and file storage with `files-backup.sh --restore` (§9). Acceptance: both restores documented and succeeded.
- **Play Store listing (Trusted Web Activity)** (needs go-live and branding; Philip's decision 12/10/2026) — wrap the PWA (22ea54d) with PWABuilder or Bubblewrap; serve `/.well-known/assetlinks.json` (Digital Asset Links); app signing key held by Philip; store listing and privacy policy. Operator steps for Philip: Play Console account (one-off US$25); a personal account created after 13/11/2023 must run a closed test with 12+ testers opted in for 14 consecutive days before production access. Acceptance: assetlinks served and tested; signed bundle built; app live on Play.
- **Re-render delivery gate package** (needs decisions 1–7 and UAT) — `/render-gate-package delivery`; open in Word and update the TOC field.
- (The former Entra-tenant blocker was retired, not resolved, by the ADR-0005 hosting pivot.)

## Log
- 2026-07-17: init-harness scaffolded the solution, wired a green `build/verify.ps1` baseline, seeded the queue from the design gate (59d96df)
- 2026-07-17: EF Core data model + ADR-0004 access control implemented; 6/6 tests (97e61d9)
- 2026-07-18: Entra External ID sign-in, provisioning and role/resource authorization implemented; 20/20 tests (c318bc5)
- 2026-07-18: Curriculum management implemented, with the interactive-render auth-resolution fix done first; 34/34 tests (863c5cb)
- 2026-07-18: Checker-FAIL on curriculum: eager principal resolution broke OIDC sign-in; made resolution lazy; 40/40 tests (1197b8c, cef5c16)
- 2026-07-18: Progress tracking implemented (completion, promotion, certificate PDF via `IBlobStorage`); 55/55 tests (9a1c898)
- 2026-07-18: Student registration/enrolment implemented, reusing `GuardianLinkService`; 81/81 tests (b85cf38)
- 2026-07-18: Checker-FAIL on registration: Student write and guardian link were not atomic; made one unit of work; 83/83 tests (d3a7e0d)
- 2026-07-18: Safeguarding & consent implemented: consent/DBS pages plus `ConsentExpiryJob` WebJob; 107/107 tests (8c458a5)
- 2026-07-19: Checker-FAIL on safeguarding: DBS sweep used a projection that bypassed the audit interceptor; fixed, expiry config wired (d23f6e4)
- 2026-07-19: Guardian-link creation flow implemented (FR-004), Admin/Teacher only, never parent self-service; 72/72 tests at the time (2867a60)
- 2026-07-19: Parent dashboard + `NotificationService` with safeguarding-critical retry/escalation implemented; 131/131 tests (1df0142)
- 2026-07-19: Reporting screens implemented (stats, promotion history, at-risk flagging); 143/143 tests (f3ebc26)
- 2026-07-19: PWA manifest/service worker implemented after checking Microsoft Learn; 147/147 tests (22ea54d)
- 2026-07-19: ASVS 5.0 access-control and WCAG 2.2 AA gate stages wired into `verify.ps1`; corrected ASVS chapter numbering (ca14c0a)
- 2026-07-19: Checker-FAIL on gate stages: checklist hash used absolute paths and no CRLF normalisation; made portable (dff3456)
- 2026-07-19: ADR-0004 §4 `DENY UPDATE/DELETE` tamper protection on `SensitiveDataAccessLogs` implemented via migration; 150/150 tests (a693916)
- 2026-07-19: Certificate/reversal investigation: documented current behaviour and added a regression test (922cc69)
- 2026-07-19: Checker-FAIL on the above: reclassified to Blocked/needs decision (unilateral business call) and corrected a false test claim in STATE.md (docs only)
- 2026-07-19: Merged parallel worktree branch (11 code commits cherry-picked) after two independent PASS reviews; 151/151 tests (2339655)
- 2026-07-19: Reconfirmed live Entra tenant blocker (placeholder config only); moved to Blocked/needs decision (63a5950)
- 2026-07-19: Re-targeted tamper-protection DENY from dedicated `VlmsAppRole` to `public`, retiring the role; 151/151 tests (7625588)
- 2026-07-20: Secrets-scan stage tightened by extending the regex fallback rather than installing gitleaks (59a069a)
- 2026-07-20: Checker-FAIL on secrets scan: three holes (quoted password regression, two false negatives) fixed (a78cc18)
- 2026-07-20: Full `openwiki/` doc-refresh across every page (22a0b7d)
- 2026-07-20: Certificate hidden from parent dashboard when its completion is reversed, per Philip's decision (925f7ab)
- 2026-07-20: Hosting pivot decided (ADR-0005): self-hosted VPS/PostgreSQL/Identity/disk/SMTP after cost review; Entra blocker retired (f72093b)
- 2026-07-20: EF Core provider migrated to PostgreSQL (Npgsql), migrations regenerated; 166/166 tests (199fa40)
- 2026-07-20: Identity implemented: Entra replaced by ASP.NET Core Identity (TOTP MFA) and self-rolled invite links; 192/192 tests (fa2be61)
- 2026-07-21: Checker-FAIL on Identity: documented ADR-0004 MFA-gated query filters, wrapped `GuardianLinkService` link+invite write; 194/194 tests (6f33fd4)
- 2026-07-21: File storage: `AzureBlobStorage` replaced by `LocalDiskBlobStorage`; 208/208 tests (8e1299c)
- 2026-07-21: Email: `AzureCommunicationEmailSender` replaced by `SmtpEmailSender` (MailKit); 219/219 tests (f8374f5)
- 2026-07-21: `Vlms.Jobs` retargeted to a systemd timer/service pair, daily 03:00 UTC (8087bcf)
- 2026-07-21: Deployment scripting added (Caddyfile, systemd unit, PostgreSQL role/grant scripts, backup, README); `UseForwardedHeaders` fix (eb51a59)
- 2026-07-21: Checker-FAIL on deployment scripting: `pg-backup.sh` permission-denied on `__EFMigrationsHistory`; granted read-only SELECT (cdd9e36)
- 2026-07-21: Verify-gate currency audit for the pivoted stack completed; last core ADR-0005 item (239829f)
- 2026-07-22: `sensitive_data_access_logs` hardened with PostgreSQL Row-Level Security (ENABLE + FORCE); 225/225 tests (588cb45)
- 2026-07-22: Teacher-facing completion-reversal flow implemented (`CompletionService.ReverseAsync`); 243/243 tests (f9edca7)
- 2026-08-05: "Records due for deletion" report implemented, widening `ConsentExpiryJob`; found no write path to `Student.Inactive` or leaving date; 254/254 tests (73eb0f8)
- 2026-08-11: WCAG 4.1.3 status-messages gap closed with `role="status"` on the form pages; 270/270 tests (def8f75)
- 2026-08-11: WCAG 1.1.1 gap closed: `aria-label="Dismiss"` on `MainLayout` dismiss glyph; 271/271 tests (81ad565)
- 2026-08-12: Manual "leaving" flag for students and teachers implemented per Philip's decision (e63a1e7); 290/290 tests (c1af6e9)
- 2026-08-12: `docs/design/` rewritten for the self-hosted stack; fixed stale SQL Server DENY wording (218f545)
- 2026-08-12: WCAG keyboard-operability review closed with no defect found (21b4a9c)
- 2026-08-12: Checker-FAIL on keyboard review: `MainLayout` dismiss control was a non-focusable span; changed to a real button (020cc37)
- 2026-08-12: WCAG error-identification review closed; `role="alert"` added to `ValidationSummary` and framework error surfaces; 314/314 tests (f14b9a3)
- 2026-08-12: Fixed three stale Azure/Entra references outside `docs/design/` found by checker grep (42d1575)
- 2026-10-11: Refreshed `openwiki/` to correct stale RLS, SMTP, storage, retention and deploy references (f9f2916)
- 2026-10-11: loop-harness updated to 0.15.0: Opus in-session working model, new ledger schema (d410457)
- 2026-10-11: Moved old-format ledger rows written by the stale 0.6.0 hook into `docs/harness/LEDGER-v1.csv` (b3cf311)
- 2026-10-11: STATE.md compacted; Done/Log narrative archived to `docs/harness/STATE-archive.md` (same commit as this entry)
- 2026-10-11: Deployment mapping (read-only) found go-live blockers: no role assignment/first Admin, no seed data, no retention purge, DataProtection keys not persisted; Next re-seeded in deploy order
- 2026-10-11: Admin role assignment + first-Admin bootstrap done (16ba130); ASVS/WCAG checklists re-reviewed and signed by Claude pending Philip's countersign
- 2026-10-11: Decision (Philip): guardian invite acceptance grants no role; an Admin approves by granting `Parent`. Recorded in functional.md FR-004; Blocked item retired
- 2026-10-11: Queued waiting-for-approval list for the role-assignment page (top of Next)
- 2026-10-12: Waiting-for-approval list done (59797a6); ASVS/WCAG checklists re-reviewed, signed by Claude pending Philip's countersign
- 2026-10-12: Seed rank ladder moved to Blocked (needs Philip's source rows); DataProtection keys persisted (037d6d9)
- 2026-10-12: EF migration bundle + `deploy/migrate.sh` done (2b0b8a5); `.gitattributes` pins LF for deploy scripts and units (b69aabb)
- 2026-10-12: Decisions (Philip): Play Store listing via Trusted Web Activity queued (d9a3ca6); VPS = DigitalOcean London 2 GiB, $12/month; app at www.varangians.co.uk; off-box backups to Cloudflare R2 (adr/0005 amendment)
- 2026-10-12: Organisation corrected in docs: Varangians is Shrewsbury Orthodox Church's youth club, not a charity (ba1b6bc); M365 tenant is the church's; domain registered with Hostinger (DNS only)
- 2026-10-12: Retention purge moved to Blocked (purge-approach decision); off-box file-storage backup done (dac8d81)
- 2026-10-12: Password reset, required email confirmation and lockout policy done (8326bd3)
- 2026-10-12: varangians.co.uk added to the church's M365 tenant (MX, SPF, DKIM live; DMARC `p=none` to dmarc@varangians.co.uk); Hostinger's second nameserver lagged on SPF and DMARC, SPF since caught up
- 2026-10-12: Decision (Philip): email via Microsoft Graph as noreply@varangians.co.uk; Entra app registered and scoped to that mailbox by Exchange RBAC, tested in/out of scope (eb46a66); teachers to get @varangians.co.uk mailboxes (9b19dc5)
- 2026-10-12: Gate runs serialised per checkout with a named mutex after two overlapping runs collided (76ca916)
- 2026-10-12: Production web hardening done (2b8a4bd); Graph email sender done, MailKit/SMTP removed (c56e9dc)
- 2026-10-13: Jobs appsettings stale Azure comment removed (abc366f); noted `deploy/systemd/vlms-jobs.service` header still says "SMTP credentials"
- 2026-10-13: gitleaks installed and wired as primary secret scanner, regex patterns kept as supplement (d02bb34)
- 2026-10-13: Ops pack markdown sources written under `docs/operations/` (8c9d9e4); open choices listed in Blocked
- 2026-10-13: Delivery-gate evidence docs written (2711a71); RTM gap: proactive parent emails (BR-005) never built
- 2026-10-13: Branding, VPS go-live, Play Store and backup restore test moved to Blocked (need Philip); delivery gate package rendered (4b52355)
- 2026-10-20: Gate broke on new advisories against `System.Security.Cryptography.Xml`/`Pkcs` 10.0.10 (published 13/10); Xml pin raised to 10.0.13, Pkcs follows (f81df92); full gate exit 0, 410/410 tests
- 2026-10-20: STATE.md review: Blocked split into Decisions needed (critical-path order) and Waiting; go-live verification checklist collected from Done footnotes; Next queued with storage-root guard, BR-005 parent notifications, stale deploy wording
- 2026-10-20: Production storage-root guard done (3e426f1); queued 10.0.13 package alignment (Philip asked)
- 2026-10-20: Proactive parent notifications (BR-005) done (180ff05); reminder schedule 28/7/1 days + day after lapse chosen at build time (LLD), reviewable by Philip
- 2026-10-20: Philip countersigned the ASVS re-review row for BR-005 (proactive parent notifications)
- 2026-10-20: Stale deploy wording fixed (ee0cb13)
