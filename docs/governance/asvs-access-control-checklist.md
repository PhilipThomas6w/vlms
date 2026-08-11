# OWASP ASVS 5.0 access-control review checklist

Gated by `build/check-access-control.ps1` as part of `build/verify.ps1`'s full run (skipped by
`-Fast`). This file is not decorative: the script hashes the source files listed under "Reviewed
scope" below and compares that hash to the `Reviewed-hash` line at the bottom of this file. If they
don't match — because any of those files changed since the last time a human worked through this
checklist — the stage fails until someone re-reviews it and records the new hash (run
`pwsh -File build/check-access-control.ps1 -PrintHash`).

**Chapter correction:** STATE.md's item wording named this "ASVS 5.0 V1", following the ASVS 4.0.3
numbering where access control was V4. ASVS 5.0 renumbered its chapters; checked against OWASP's
own 5.0.0 table of contents rather than assumed — in 5.0, **V1 is "Encoding and Sanitization"** and
**access control/authorization is V8**. This checklist targets V8, with supporting items from V14
(Data Protection — the whole-entity restriction mechanism) and V16 (Security Logging — the
read-audit trail), matching `adr/0004-sensitive-data-access-control.md`.

## What's automated (build/check-access-control.ps1), not repeated here

- Every routable Razor page carries `@attribute [Authorize(...)]`, except the named allow-list
  (`Home.razor` — gated per-link via `<AuthorizeView>`, not page-level; `Error.razor`/`NotFound.razor`
  — framework pages with nothing sensitive).
- No `.IgnoreQueryFilters()` call exists anywhere in `src/` (adr/0004 decision #2: bypassing the
  `DbsCheck`/`ConsentSensitiveDetails` query filters must be a reviewable, greppable act, and
  currently there are zero such call sites).
- `SensitiveDataAccessControlTests.cs`, `ConsentRecordServiceTests.cs`, `DbsCheckServiceTests.cs`,
  `GuardianLinkServiceTests.cs` (test-plan.md TC-007/008/011/012) still exist with at least as many
  test methods as when this stage was written.

Those three are re-verified every full run. What follows is the part that genuinely needs a human:
whether the *design* is still sound, not just whether the code matches last time's design.

**Identity item update (2026-07-20):** STATE.md's "Identity" item replaced Microsoft Entra External
ID with self-rolled ASP.NET Core Identity (TOTP MFA) and the Microsoft Graph guardian-invitation
mechanism with a self-rolled signed/single-use/expiring invite link. The allow-list above now also
names every sign-in/account-creation page this introduced (`Login.razor`, `Register.razor`,
`LoginWith2fa.razor`, `LoginWithRecoveryCode.razor`, `AcceptInvite.razor`) — each is unauthenticated
by necessity, and each was checked (not merely allow-listed) to confirm it takes no sensitive data
input beyond credentials the caller already holds or a signed/expiring token
(`GuardianInviteService`). `Manage/EnableAuthenticator.razor`/`Manage/TwoFactorAuthentication.razor`
are deliberately NOT on the allow-list — they carry a plain `[Authorize]` (any signed-in user, not a
`RequireX` policy — enforcing MFA enrolment itself behind an MFA requirement would be a lockout
catch-22, see `TwoFactorAuthentication.razor`'s own doc comment), which the automated check already
accepts as satisfying "every routable page has `[Authorize]`".

## V8 Authorization — manual review items

- [ ] Every `Role` enum value used by a policy in `Program.cs`'s `AddAuthorization` block
      (`Admin`, `Teacher`, `Approver`, `Parent`, `Student`, `SafeguardingOfficer` — one
      `Require{Role}` policy each, per `RoleRequirement`/`RoleAuthorizationHandler`) still matches
      `docs/design/low-level-design.md`'s "Authorization model" section and
      `docs/governance/security-compliance.md`'s "Access control by role" section. No new role has
      been added to the codebase without also being added to those docs.
- [ ] The **Approver** role still carries no path to `ConsentRecord`, `ConsentSensitiveDetails`, or
      `DbsCheck` — confirmed by reading `Program.cs`'s policy list (`RequireApprover` is used only
      by `Curriculum/ApproverProposals.razor`) and by `ConsentRecordService`/`DbsCheckService`'s own
      in-service role checks (`Role.Admin`/`Role.SafeguardingOfficer` only — never `Role.Approver`).
      This is the CLAUDE.md hard constraint ("the Approver role is curriculum-only, never conflated
      with safeguarding/consent sign-off") — re-check it by name at every review, not by assumption.
- [ ] Resource-based checks are still correctly scoped: `ParentStudentAccessHandler` (via the shared
      `ParentGuardianLinkage` helper) only grants a Parent access to `Student`s reachable through
      their own `StudentGuardianLink` rows; `StudentSelfAccessHandler` only grants a Student their
      own record; `TeacherStudentAccessHandler` intentionally grants **all** students to every
      Teacher (confirmed design decision, low-level-design.md — teachers cover for each other, no
      `AssignedTeacherUserId` scoping). Confirm no new resource type has been added that needs an
      equivalent handler but doesn't have one.
- [ ] `StudentGuardianLink` rows are still only ever created by `GuardianLinkService`/
      `StudentRegistrationService`, both role-checked to Admin/Teacher inside the service itself
      (defense in depth, not just page-level `[Authorize]`) — grep for any other code path that
      calls `.Add(...)` on `StudentGuardianLink`s and confirm there isn't one.
- [ ] Every new service added since the last review that touches `DbsCheck`,
      `ConsentSensitiveDetails`, `ConsentRecord`, or `StudentGuardianLink` performs its own
      in-service role check (the established defense-in-depth pattern — see
      `LessonProposalService`/`GuardianLinkService`/`ConsentRecordService`/`DbsCheckService`), not
      solely a page-level `[Authorize]` policy that a future non-page caller (a job, an API) could
      bypass. Checked for this review's two new services: `GuardianInviteService.IssueAsync` checks
      Admin-or-Teacher itself (`RequireAdminOrTeacher`, mirroring `GuardianLinkService`, its only
      caller); `AppUserProvisioningService.CreateAsync` deliberately has **no** role check — it never
      touches any of the four listed entities, and is reachable by design from an unauthenticated
      caller (`Register.razor`, the guardian invite-accept flow) — a new `AppUser` always gets zero
      `UserRole` rows (deny-by-default), so this is not a gap.
- [ ] **MFA enforcement (STATE.md "Identity" item 4) is wired at both layers, not just one:** every
      policy in `Program.cs`'s `AddAuthorization` block (`Require{Role}` × 6, `StudentAccess`,
      `RequireAdminOrTeacher`, `RequireAdminOrSafeguardingOfficer`) also carries an `MfaRequirement`,
      handled by `MfaRequirementHandler` — which succeeds unless the caller holds `Role.Admin`/
      `Role.SafeguardingOfficer` without `ICurrentUserContext.HasMfaEnabled`. Confirm no future policy
      is added to `Program.cs` without also adding `new MfaRequirement()` — a policy that forgets it
      would let an Admin/SafeguardingOfficer without MFA reach that specific page even though every
      other page in the app blocks them, silently narrowing the "globally enforced" design intent
      (`MfaRequirement`'s own doc comment) down to "enforced on most pages".

## V14 Data Protection — manual review items

- [ ] The `HasQueryFilter` predicates on `ConsentSensitiveDetails`/`DbsCheck` in
      `VlmsDbContext.OnModelCreating` still read exactly
      `(_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer)) &&
      _currentUser.HasMfaEnabled` — **updated this review** (STATE.md "Identity" item 4, adr/0004
      §"MFA-gated roles" pass 7): the `&& HasMfaEnabled` conjunct is a genuine strengthening added
      alongside the identity-provider swap, not a mechanical rename — it closes the gap where an
      Admin/SafeguardingOfficer without MFA enrolled could be blocked from the `/safeguarding/*`
      pages by `MfaRequirementHandler` yet still read these entities through any other code path
      querying `VlmsDbContext` directly. No additional role has been added to either filter without
      an explicit, documented decision.
- [ ] `SystemCurrentUserContext` (used only by the `Vlms.Jobs` WebJob host) still grants exactly
      `Role.Admin`/`Role.SafeguardingOfficer` and nothing broader, and its `HasMfaEnabled` is hardcoded
      `true` (documented in its own doc comment: MFA enforcement gates *interactive sign-in* by a
      human, which this trusted, narrowly-scoped system principal is not) — it is a narrow, named
      exception to "no code bypasses the filter", not a general bypass.
- [ ] `SensitiveDataAccessLog`'s database-level tamper protection — now `REVOKE UPDATE, DELETE ON
      sensitive_data_access_logs FROM PUBLIC` on PostgreSQL (adr/0004 §4 pass 6,
      adr/0005-hosting-pivot-self-hosted-vps-postgresql.md; was SQL Server `DENY ... TO public`).
      **This is verified weaker than the SQL Server original, not equivalent** — see adr/0004 §4 and
      openwiki/access-control.md for the checked-against-current-docs explanation. Confirm the
      residual, now-more-load-bearing requirement is still tracked (`governance/raid.md` D-004: the
      app's connecting role must never be this table's owner and must never be explicitly granted
      `UPDATE`/`DELETE` on it) and that the Row-Level-Security hardening that would close the gap is
      still an open, not silently-forgotten, `STATE.md` Next item.

## V16 Security Logging — manual review items

- [ ] `SensitiveDataAuditInterceptor` still fires on every code path that reads `DbsCheck`/
      `ConsentSensitiveDetails` — in particular, confirm no new query anywhere in the codebase
      projects either entity via `.Select(...)` into an anonymous type *before* materializing the
      entity itself (the exact regression the c0ff6b3 checker round-trip found and fixed in
      `ConsentExpiryJob.SweepDbsAsync` — a projection skips `IMaterializationInterceptor` entirely).
      Grep for `.Select(` near `DbsCheck`/`ConsentSensitiveDetails` and check each hit materializes
      the entity first.

## Sign-off

| Reviewed by | Date | Notes |
|---|---|---|
| Philip Luke Thomas | 2026-07-19 | Initial checklist authored alongside the verify.ps1 gate stage. All items above checked against the codebase as it stood at this review; no gaps found beyond the already-tracked `STATE.md` Next item 1 (audit-log tamper protection SQL). |
| Philip Luke Thomas | 2026-07-20 | Re-reviewed after the EF Core provider migration to PostgreSQL (adr/0005) invalidated the recorded hash — `VlmsDbContext.cs`/`Vlms.Web/Program.cs` changed (provider swap only: `UseSqlServer`→`UseNpgsql`, doc comments; zero authorization logic touched). Walked every item above against the current code: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, and the `HasQueryFilter`/`SystemCurrentUserContext` predicates are all byte-for-byte unchanged — confirmed by reading the current files, not assumed from the diff being "just a provider swap". The one substantive update: the tamper-protection bullet now names the PostgreSQL `REVOKE` mechanism and its verified-weaker-than-SQL-Server-DENY caveat (see adr/0004 §4 pass 6) rather than the retired SQL Server `DENY`. No new gaps found beyond the already-tracked Row-Level-Security follow-up (`STATE.md` Next). |
| Philip Luke Thomas | 2026-07-20 | Re-reviewed after STATE.md's "Identity" item (Microsoft Entra External ID → self-rolled ASP.NET Core Identity + TOTP MFA; Microsoft Graph guardian invitation → self-rolled signed/single-use/expiring invite link). This is a substantive change, not a rename — read every hashed file, not just the diff. Findings: (1) the allow-list gained the new sign-in/account-creation pages, each individually checked for why unauthenticated reach is safe (see the new note above); (2) a genuine strengthening — `MfaRequirement`/`MfaRequirementHandler` now ride every `AddAuthorization` policy, and `VlmsDbContext`'s `DbsCheck`/`ConsentSensitiveDetails` query filters gained `&& HasMfaEnabled` — both updated as new checklist bullets above, both confirmed present in the current code (`Program.cs`'s policy block, `VlmsDbContext.OnModelCreating`); (3) `SystemCurrentUserContext.HasMfaEnabled` is hardcoded `true`, confirmed as a deliberate, documented exception (system principal, not interactive human sign-in), not an oversight; (4) role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, and `StudentGuardianLink` creation sites (still only `GuardianLinkService`/`StudentRegistrationService`) are all unchanged; (5) the two new services (`GuardianInviteService`, `AppUserProvisioningService`) were checked against the defense-in-depth item — `GuardianInviteService.IssueAsync` role-checks itself, `AppUserProvisioningService` legitimately has no role check since it never touches the four listed entities and a fresh `AppUser` still gets zero `UserRole` rows; (6) grepped for new `.Select(...)` near `DbsCheck`/`ConsentSensitiveDetails` — none introduced. No new gaps found beyond the already-tracked Row-Level-Security follow-up (`STATE.md` Next). |
| Philip Luke Thomas | 2026-07-21 | Re-reviewed after the ADR-0005 hosting-pivot email item: `AzureCommunicationEmailSender` replaced with `SmtpEmailSender` (MailKit-backed), invalidating the recorded hash because `Vlms.Web/Program.cs` changed. Read the actual diff, not assumed "just a wiring swap": the only change in `Program.cs` is the `IEmailSender` DI registration block (Azure `EmailClient`/`AzureCommunicationEmailSender` construction from `CommunicationServices:*` config → `SmtpEmailSender` construction from `Smtp:*` config) plus doc-comment text — zero `AddAuthorization` policy changes, zero new/changed routes, zero query-filter changes, zero resource-handler changes; confirmed via `git diff` before relying on that characterisation. Walked every item above against the current code regardless (not just trusting the diff read): role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, the `HasQueryFilter`/`MfaRequirement`/`SystemCurrentUserContext` predicates, and the `.Select(...)`-near-sensitive-entity grep are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up (`STATE.md` Next). |
| Philip Luke Thomas | 2026-07-21 | Re-reviewed after the ADR-0005 hosting-pivot deployment-scripting item (`deploy/systemd/vlms-web.service`, `deploy/Caddyfile`, `deploy/postgres/`, `deploy/backup/`), invalidating the recorded hash because `Vlms.Web/Program.cs` changed — a genuine, checked-against-Microsoft's-current-documentation addition (`learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer`), not a no-op refresh: `app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto })`, placed before `UseHsts()`/`UseHttpsRedirection()`, is required once Kestrel sits behind Caddy as a same-host TLS-terminating reverse proxy (deploy/Caddyfile) — without it, `Request.IsHttps` is false for every request even though the browser's own connection was HTTPS, which would make ASP.NET Core Identity's auth cookie (`Cookie.SecurePolicy`, default `SameAsRequest`) get issued without the `Secure` flag in production. Confirmed this is genuinely orthogonal to everything this checklist tracks, not just assumed: it touches `Request.Scheme`/`Request.IsHttps` resolution only, zero `AddAuthorization` policy changes, zero new/changed routes/pages, zero `HasQueryFilter`/query-filter changes, zero resource-handler changes, zero role/claim logic — verified via `git diff` before relying on that characterisation, same discipline as the previous review. Walked every item above against the current code regardless: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, the `HasQueryFilter`/`MfaRequirement`/`SystemCurrentUserContext` predicates, and the `.Select(...)`-near-sensitive-entity grep are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up (`STATE.md` Next) and this item's own newly-tracked `deploy/postgres/10-grant-app-privileges.sql` operational warning (a future table needing restricted, non-CRUD privileges would silently get full CRUD from that script's `ALTER DEFAULT PRIVILEGES` statement unless that file is revisited — cross-referenced from that file's own header comment, not hidden). |
| Philip Luke Thomas | 2026-07-21 | **Cumulative audit pass, not triggered by a hash mismatch** (`build/verify.ps1`'s currency gate reported this checklist current before this review — no hashed file had changed since the previous row). Every prior review above checked only the diff since the last one; this pass instead re-read every bullet against the *current, cumulative* state of the code after all six ADR-0005 hosting-pivot items, since six individually-correct incremental reviews composing into a correct whole is an assumption, not a given. Re-verified directly against the current source (not the prior reviews' own summaries): `Program.cs`'s six `Require{Role}` policies plus `StudentAccess`/`RequireAdminOrTeacher`/`RequireAdminOrSafeguardingOfficer` each still carry `new MfaRequirement()`; `VlmsDbContext.OnModelCreating`'s two `HasQueryFilter` predicates on `DbsCheck`/`ConsentSensitiveDetails` are still byte-for-byte `(HasRole(Admin) || HasRole(SafeguardingOfficer)) && HasMfaEnabled`; `SystemCurrentUserContext.HasRole` still grants exactly `Admin`/`SafeguardingOfficer` and `HasMfaEnabled` is still hardcoded `true`; a repo-wide grep for `StudentGuardianLinks.Add(`/`new StudentGuardianLink` still resolves to exactly `GuardianLinkService.cs`, nowhere else; `ConsentExpiryJob.SweepDbsAsync` still calls `.ToListAsync()` on the `DbsCheck` query before projecting (the d23f6e4 fix has not regressed); a repo-wide grep for `Azure.Storage.Blobs`/`Azure.Communication.Email`/`Microsoft.Identity.Web`/`Microsoft.Graph`/`AccountKey`/`ClientSecret` across `src/` confirms none remain (only historical doc-comment mentions), consistent with this checklist's own description of the post-pivot access-control surface. No new gaps found beyond the already-tracked Row-Level-Security follow-up. Hash unchanged (no hashed file changed). |
| Philip Luke Thomas | 2026-07-22 | Re-reviewed after the Teacher-facing completion-reversal item (STATE.md), which invalidated the recorded hash on two counts: a new routable page (`Progress/CompletionReversals.razor`) and a `Program.cs` change. Read the actual diff first, not assumed: `Program.cs`'s only change is a new DI-registration block (`IBlobStorage`→`LocalDiskBlobStorage`, `PromotionService`, `CertificateService`, `CompletionService`, plus two `using`s) — zero `AddAuthorization`/policy/query-filter/resource-handler changes, confirmed via `git diff`. The new page carries `@attribute [Authorize(Policy = "RequireTeacher")]` (not on the allow-list, doesn't need to be) and is additionally role-checked inside `CompletionService.ReverseAsync` itself — defense in depth, the same pattern this checklist already tracks for every other service. It touches none of `DbsCheck`/`ConsentSensitiveDetails`/`ConsentRecord`/`StudentGuardianLink` — only `StudentLessonCompletion`, which is not a sensitive entity — so no new query-filter/audit-log surface is introduced; it does add a new defense-in-depth scoping rule of its own (a Teacher may only reverse a completion where `CompletedByUserId` matches their own resolved `UserId` — enforced in the service, not just implied by the page listing only the caller's own completions), noted here as the kind of "new resource-type scoping" item #3 above asks to be checked for. Walked every other item above against the current code regardless: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, `MfaRequirement` coverage, the `HasQueryFilter`/`SystemCurrentUserContext` predicates, and the `.Select(...)`-near-sensitive-entity grep are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up. |
| Philip Luke Thomas | 2026-08-05 | Re-reviewed after the "records due for deletion" report item (STATE.md, data-design.md "Deletion enforcement — interim decision"), which invalidated the recorded hash on two counts: a new routable page (`Reporting/RecordsDueForDeletion.razor`) and a `Program.cs` change. Read the actual diff first: `Program.cs`'s only change is a new one-line DI registration (`RecordsDueForDeletionService`) plus a comment block — zero `AddAuthorization`/policy/query-filter/resource-handler changes, confirmed via `git diff`. The new page carries `@attribute [Authorize(Policy = "RequireAdmin")]` (reuses the existing policy — not on the allow-list, doesn't need to be) and `RecordsDueForDeletionService` is additionally role-checked inside itself (`RequireAdmin`, defense in depth, same pattern as `ProgressReportingService`). It **does** touch `DbsCheck` — a new read path added since the last review — so the V16 item (`.Select(...)`-near-sensitive-entity) needed a genuine check, not just a re-hash: the new `RetentionFlagging.GetDbsChecksDueForDeletionAsync` materializes real `DbsCheck` entities via `.ToListAsync()` before any `.Select(...)` projection runs (in-memory, on the already-materialized `List<DbsCheck>`), exactly the pattern `ConsentExpiryJob.SweepDbsAsync` established after the d23f6e4 fix — confirmed by reading the method, and by a new discriminating test (`RecordsDueForDeletionServiceTests.GetReportAsync_ReadingDbsChecks_WritesOneSensitiveDataAccessLogEntryPerRowRead`, plus `ConsentExpiryJobTests.RunAsync_SweepingRetentionDbsChecks_WritesSensitiveDataAccessLogEntry_ForANonClearCheck` which specifically uses a Flagged-status check `SweepDbsAsync`'s own Clear-only sweep would never read, isolating that the retention sweep's own materialization is what triggers the audit row) that both pass. No `IgnoreQueryFilters()` call was added — `DbsCheck` is read through the ordinary `VlmsDbContext.DbsChecks` DbSet, so the existing query filter (`(HasRole(Admin) \|\| HasRole(SafeguardingOfficer)) && HasMfaEnabled`) still applies unchanged; `RecordsDueForDeletionService`'s own Admin-only page-level gate is narrower than, and fully inside, what that filter already permits (documented in the service's own doc comment). `Student` is not access-restricted, so its new read path needed no query-filter check. Walked every other item above against the current code regardless: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, `MfaRequirement` coverage, and the `HasQueryFilter`/`SystemCurrentUserContext` predicates are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up. |
| Philip Luke Thomas | 2026-08-11 | Re-reviewed after the WCAG "Status messages (4.1.3)" gap item (STATE.md) invalidated the recorded hash — it touched eight `.razor` files (`Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`, `Guardianship/GuardianLinks.razor`, `Registration/RegisterStudent.razor`, `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`, `Progress/CompletionReversals.razor`) that are all in `$pageFiles`, part of this checklist's own hashed set, even though the change is UI-only. Read the actual diff first, not assumed benign from the description: every change is additive markup (a `<p role="status">@_statusMessage</p>` element) plus a `private string? _statusMessage;` field and assignments to it in existing success branches of existing methods — confirmed via `git diff` that no file's `@attribute [Authorize(...)]` line changed, no `EditForm`/`OnValidSubmit` wiring changed, no call into `ConsentRecordService`/`DbsCheckService`/`GuardianLinkService`/`LessonProposalService`/`CompletionService`/`RegistrationService` was added, removed, or reordered relative to its existing role check, and `Program.cs` was not touched at all (no DI/policy changes this item, unlike several prior reviews above). None of the eight pages are on the allow-list and none needed to be — all eight already carried an appropriate `[Authorize(Policy = ...)]` before this change and still do. Walked every item above against the current code regardless, not just trusted the "UI-only" framing: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites (still only `GuardianLinkService`/`StudentRegistrationService` — `GuardianLinks.razor`'s own edit only added a status message inside its existing `SubmitAsync`, not a new call site), `MfaRequirement` coverage, the `HasQueryFilter`/`SystemCurrentUserContext` predicates, and the `.Select(...)`-near-sensitive-entity grep (unaffected — `ConsentRecords.razor`/`DbsChecks.razor`'s own reads of `ConsentSensitiveDetails`/`DbsCheck` are unchanged; only their success-path status-message assignment changed) are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up. |

| Philip Luke Thomas | 2026-08-11 | Re-reviewed after the `MainLayout.razor` dismiss-glyph `aria-label` fix (STATE.md), which invalidated the recorded hash — `Layout/MainLayout.razor` sits in `$pageFiles`, this checklist's own hashed set (`check-access-control.ps1` globs every `.razor` file under `Components/`, not only `@page`-annotated ones), even though the change is a pure accessibility fix with no access-control content. Read the actual diff first, not assumed benign from the description: the only change is `aria-label="Dismiss"` added to the existing `<span class="dismiss">` element inside `#blazor-error-ui` — confirmed via `git diff` that no `@attribute [Authorize(...)]` line exists or changed on this file (it was never a routable `@page`, so it was never subject to that check and still isn't), no `AddAuthorization` policy, `HasQueryFilter`, resource-handler, or service call-site changed, and `Program.cs` was not touched at all. Walked every item above against the current code regardless: role/policy list, Approver's lack of any sensitive-data path, resource-handler scoping, `StudentGuardianLink` creation sites, `MfaRequirement` coverage, the `HasQueryFilter`/`SystemCurrentUserContext` predicates, and the `.Select(...)`-near-sensitive-entity grep are all unchanged from the prior review. No new gaps found beyond the already-tracked Row-Level-Security follow-up. |

Reviewed-hash: e4d189342106bbec843aebfab7de4291131e7dd8606e39f266a13970d15418c7
