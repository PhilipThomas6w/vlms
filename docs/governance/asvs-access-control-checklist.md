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
      bypass.

## V14 Data Protection — manual review items

- [ ] The `HasQueryFilter` predicates on `ConsentSensitiveDetails`/`DbsCheck` in
      `VlmsDbContext.OnModelCreating` still read exactly
      `_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer)` — no
      additional role has been added to either filter without an explicit, documented decision.
- [ ] `SystemCurrentUserContext` (used only by the `Vlms.Jobs` WebJob host) still grants exactly
      `Role.Admin`/`Role.SafeguardingOfficer` and nothing broader — it is a narrow, named exception
      to "no code bypasses the filter", not a general bypass.
- [ ] `SensitiveDataAccessLog`'s database-level tamper protection (`DENY UPDATE`/`DELETE` for the
      app's SQL principal, per adr/0004 §4) — confirm current status against `STATE.md` Next item 1;
      this is tracked there, not silently assumed done by this checklist.

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

Reviewed-hash: 6145c2dd7d624087a624c54eb69c7ccec41056b5bd070accb0e9ca41072856d2
