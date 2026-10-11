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

## In progress
_(none)_

## Next
- [ ] **Seed rank ladder + curriculum** — no `HasData`/`InsertData` seed exists. Idempotent seed via migration or first-run job; source rows need confirming with Philip. Acceptance: fresh DB has the rank ladder; test.
- [ ] **Persist DataProtection keys** — `Program.cs:51` uses bare `AddDataProtection()` and the `vlms` service user has no home dir. `PersistKeysToFileSystem` to a config-driven `vlms`-writable path (e.g. `/var/lib/vlms/keys`, 0700), `SetApplicationName`; update `deploy/README.md` and unit. Acceptance: config test; restart keeps cookies/invite tokens valid (verify on host).
- [ ] **EF migration bundle + deploy script** — migrations are currently applied by hand with the SDK (`deploy/README.md` §3). `dotnet ef migrations bundle` (or idempotent SQL) run as `vlms_owner` from a `deploy/` script. Acceptance: README §3 no longer needs the SDK on the server.
- [ ] **Retention purge** — only flagging exists (`RecordsDueForDeletionService.cs:30`); project law requires hard delete at 3 years (students) / 6 years (DBS, audit log). Audit-log purge needs a separate `BYPASSRLS` maintenance role (adr/0004 pass 8); `vlms_app` must never get DELETE on `sensitive_data_access_logs`. Needs decision: purge approach (see Blocked). Acceptance: tests for retention boundaries; purge deletes only due records.
- [ ] **Off-box backup of file storage** — `deploy/README.md` §9: no off-box backup of `LocalDiskBlobStorage` files; `pg-backup.sh` covers PostgreSQL only. Extend `deploy/backup/` with a sync of `Storage:LocalRootPath` to the S3-compatible bucket plus a systemd timer. Acceptance: script + unit exist, README §9 updated, restore steps documented.
- [ ] **Password reset + Identity hardening** — no forgot/reset-password pages; `RequireConfirmedAccount = false` (`Program.cs:229`); no explicit lockout policy. Add reset via SMTP; set lockout/confirmed-email policy deliberately. Acceptance: tests; policy documented.
- [ ] **Production web hardening** — `AllowedHosts` is `*`; HSTS default max-age; no rate limiting. Config-driven `AllowedHosts`, HSTS max-age, rate limiting on login/invite endpoints. Acceptance: tests; gate exits 0.
- [ ] **Jobs appsettings stale comment** — `src/Vlms.Jobs/appsettings.json` `_comment` still says Azure App Service WebJob. Acceptance: no Azure wording in `src/Vlms.Jobs` (excluding bin/obj).
- [ ] **Real secret scanning** — install gitleaks so `build/check-secrets.ps1` takes its `Get-Command gitleaks` path instead of the regex fallback. Acceptance: gate output reads "secret scan (gitleaks): clean", not "regex fallback".
- [ ] **Ops pack markdown sources** — runbook, monitoring/alerting/uptime + journald retention, DR/BCP, release and rollback (incl. migrations, no staging slot), config reference, SSH/firewall/unattended-upgrades hardening; jobs/backup timers run in UTC — state explicitly. Acceptance: sources under `docs/` with `[TBC – Philip]` where undecided.
- [ ] **Delivery-gate evidence docs** — UAT report template, TC-001..TC-014 results table, DPIA draft (`raid.md` D-003) and privacy-notice draft for Philip's review, RTM completeness check (`docs/quality/traceability.md`). Acceptance: markdown sources exist so `/render-gate-package delivery` can run.
- [ ] **Branding + WCAG colour contrast** — VLMS palette and icon replacing the template `src/Vlms.Web/wwwroot/app.css` and placeholder `wwwroot/icons/icon.svg`, then close the open "Colour contrast (1.4.3/1.4.11)" item in `docs/quality/wcag-2.2-aa-checklist.md`. Needs decision: palette (see Blocked). Acceptance: branding landed, contrast verified at 4.5:1 text / 3:1 UI, checklist item closed, gate exits 0.
- [ ] **VPS go-live** — operator (Philip) work with a human-in-the-loop component; depends on every code item above plus the Blocked hosting decisions; harden SSH/firewall/unattended-upgrades per the ops pack. Provision the VPS and deploy per `deploy/README.md` (§1 OS prep, §2 install PostgreSQL/Caddy/.NET, §3 roles and grants, §4 publish, §5 systemd units, §6 Caddyfile, §7 verification checklist); then smoke-test each role's core journey in production and confirm TLS via Caddy. Acceptance: app reachable on the production hostname; smoke test recorded.
- [ ] **Backup restore test** — restore a `pg_dump` backup (`deploy/backup/pg-backup.sh`) from the off-box S3-compatible store into a scratch database (`deploy/README.md` §7, last bullet), and the file-storage backup likewise. Note `deploy/README.md` §9: no off-box backup of `LocalDiskBlobStorage` files exists yet, so that must be built first. Acceptance: restore documented and succeeded for both.
- [ ] **Render the delivery gate package** — `gates/delivery/` is empty (only `gates/design/` has 13 `.docx`). Run `/render-gate-package delivery` from `docs/` markdown, never hand-authored. Per the gate taxonomy, `docs/` has no source for the delivery-only UAT report (`VLMS-UAT`) or operations pack (runbook, monitoring, DR/BCP, release, config); `docs/delivery-plan.md`'s exit criteria also need the DPIA (`raid.md` D-003), TC results and a complete RTM. Author those markdown sources first, or render with `[TBC – owner]` gaps. Acceptance: package rendered from `docs/` markdown; `build/verify.ps1` exits 0.

## Blocked / needs decision
- Palette/branding: Philip to choose the VLMS palette and icon direction before the branding item can start.
- VPS provider, region, budget, domain and off-box storage (Cloudflare R2 vs Backblaze B2) are undecided; ADR-0005 requires Philip's explicit go-ahead before any provisioning.
- SMTP target (Microsoft 365 mailbox vs transactional provider, `raid.md` D-002) and who controls DNS for SPF/DKIM/DMARC.
- Retention-purge approach: manually run `BYPASSRLS` maintenance role vs scheduled job (adr/0004 pass 8).
- Whether ICO registration applies and privacy-notice wording — Philip to verify; not asserted here.
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
