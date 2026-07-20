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

Reviewed-hash: 3f6cff38815125753a49dbf02c7012ec74144292096675e455ac82e6118108ed
