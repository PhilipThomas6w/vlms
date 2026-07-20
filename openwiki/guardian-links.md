# Guardian-link creation

Design source: `docs/requirements/functional.md` FR-004, `docs/design/data-design.md` "Guardian link verification", `docs/design/low-level-design.md` "Authorization model". STATE.md: implements the Next/In-progress item "Guardian-link creation flow (Admin/Teacher only, at student registration)".

`GuardianLinkService` (`src/Vlms.Infrastructure/Guardianship/GuardianLinkService.cs`) creates the `StudentGuardianLink` join row a Parent's later access to a `Student` depends on (`ParentStudentAccessHandler` — see [authentication-authorization.md](authentication-authorization.md)).

## Schema check before building (same discipline as prior increments)

`StudentGuardianLink` already had everything needed — no migration in this change. It was fully modelled from the EF Core data-model increment: composite primary key `(StudentId, ParentGuardianId)` (`VlmsDbContext.OnModelCreating`), which already makes a duplicate link impossible at the database level, plus `CreatedByUserId`. `dotnet ef migrations has-pending-model-changes` confirmed no drift.

## `GuardianLinkService`

Role-checked inside the service itself (defense in depth, same pattern as `LessonProposalService`/`CompletionService`) — **Admin or Teacher only**. This is a hard constraint (CLAUDE.md Project Law; FR-004; data-design.md): a `StudentGuardianLink` must never be created by parent self-service. There is no code path anywhere in this codebase — service method or page — that lets a Parent create their own link.

Two entry points, matching data-design.md's "the Admin/Teacher enters the guardian's details" wording:

- **`RegisterGuardianAndLinkAsync(studentId, guardianName, contactInfo, isPrimary)`** — the common case at a new student's registration: no `ParentGuardian` record exists yet, so this creates one and links it in a single call.
- **`CreateLinkAsync(studentId, parentGuardianId)`** — links an *existing* `ParentGuardian` (e.g. the same parent's second child) without creating a duplicate `ParentGuardian` row.

Both throw `InvalidOperationException` for an unknown `Student`/`ParentGuardian` id (via `SingleAsync`, same convention as `CompletionService`), and `CreateLinkAsync` throws a clear `InvalidOperationException` if the link already exists (checked explicitly before insert, rather than surfacing a raw unique-constraint violation from the composite key).

## Guardian invite issuance (STATE.md "Identity" item 5, added after this page was first built)

`data-design.md`'s "Guardian link verification" section originally described the Admin/Teacher issuing "an Entra External ID invitation already scoped to the correct Student record" — no such mechanism existed at first (deliberately flagged, not silently dropped, in the original increment's own scope note), since building it needed a live Entra tenant/Microsoft Graph and the hosting pivot (adr/0005) later moved this project off Entra entirely. It's now built as a **self-rolled signed/single-use/expiring invite link**, wired straight into `GuardianLinkService` rather than left as a separate step a caller must remember:

- Both `RegisterGuardianAndLinkAsync` and `CreateLinkAsync` call a private `IssueInviteIfUnregisteredAsync` immediately after the `StudentGuardianLink` row is created/saved.
- **The trigger condition is `guardian.AppUserId is null`, not "new vs existing guardian"** — a guardian who already has an Identity account from an earlier child needs no new invite when a second child is linked: `ParentGuardianLinkage`'s existing join already grants them access to the new link via their one existing `AppUserId` the moment it exists. Issuing a second invite there would be pointless, and — if somehow accepted — would throw, since `GuardianInviteService.AcceptAsync` rejects accepting an invite for a guardian who already has an account.
- The actual issuance (`GuardianInvite` row creation, signed-token generation, sending the email) lives in `GuardianInviteService.IssueAsync` (`src/Vlms.Infrastructure/Guardianship/GuardianInviteService.cs`), injected into `GuardianLinkService` as a constructor dependency — see [authentication-authorization.md](authentication-authorization.md)'s "Guardian invite issuance" section for the token design (ASP.NET Core Data Protection's `ITimeLimitedDataProtector`, not a bespoke HMAC scheme) and [domain.md](domain.md) for the `GuardianInvite` entity itself.
- **Accepting an invite** — `Components/Pages/Guardianship/AcceptInvite.razor` (`/guardianship/accept-invite`), deliberately unauthenticated (the caller clicking an emailed link is by definition not yet signed in) — validates the token via `GuardianInviteService.AcceptAsync`, which creates the account (`AppUserProvisioningService`) and marks the invite consumed atomically, then the page itself calls `SignInManager.SignInAsync` directly (this step can't live in Infrastructure — it needs `HttpContext` to write the auth cookie) and redirects to `/parent/dashboard`.
- `GuardianInviteService.IssueAsync` role-checks itself (Admin or Teacher — defense in depth, the same pattern `GuardianLinkService` already uses), even though its only current caller has already checked.

## Authorization: a new `AnyRoleRequirement`/`AnyRoleAuthorizationHandler`

The guardian-links page needs "Admin OR Teacher", which the existing single-role machinery can't express: `Program.cs`'s `foreach (var role in Enum.GetValues<Role>())` loop wires exactly one `RequireX` policy per role, each requiring that one role. Added `AnyRoleRequirement`/`AnyRoleAuthorizationHandler` (`src/Vlms.Infrastructure/Authorization/`) — additive, not a replacement for `RoleRequirement`/`RoleAuthorizationHandler`, which are unchanged. `Program.cs` wires one new policy, `"RequireAdminOrTeacher"`, from it.

## Page

`src/Vlms.Web/Components/Pages/Guardianship/GuardianLinks.razor` (`/guardianship/links`), gated by `[Authorize(Policy = "RequireAdminOrTeacher")]` — no ad hoc role checks at the page level, same convention as the curriculum pages. Lists existing links, and a form to either link an existing guardian or register a new one, matching the service's two entry points. Linked from `Home.razor` via `<AuthorizeView Policy="RequireAdminOrTeacher">`.

## Scope note (deliberate, at the time this page was built)

This service/page implemented only the guardian-link creation flow FR-004 asks for — not `Student` creation or full `ParentGuardian` CRUD/registration. In particular it did **not** open a `Student`'s first `StudentRankProgress` row — `PromotionService`'s doc comment (see [progress-tracking.md](progress-tracking.md)) had flagged that precondition as expected to land with "student registration". That gap is now closed by [student-registration.md](student-registration.md)'s `StudentRegistrationService`, which reuses this service's two entry points (`RegisterGuardianAndLinkAsync`/`CreateLinkAsync`) rather than duplicating them — this page (`GuardianLinks.razor`) still exists in its own right for linking a guardian to an already-registered `Student` (e.g. correcting/adding a link after the fact).

## Tests

`tests/Vlms.Tests/Infrastructure/GuardianLinkServiceTests.cs` (SQLite-in-memory-via-DI pattern, same as `LessonProposalServiceTests`): both entry points, by Admin and by Teacher; denial for Parent and Approver (with a dedicated assertion that a Parent's attempt creates no row — the hard "never parent self-service" constraint); duplicate-link rejection; the same guardian linking to two different students (not a duplicate — composite key is per-student); unknown-student/unknown-guardian rejection; blank guardian name rejection.

`tests/Vlms.Tests/Authorization/AnyRoleAuthorizationHandlerTests.cs`: succeeds on either listed role, fails when the caller holds neither, and a constructor guard against an empty role list.

`tests/Vlms.Tests/Infrastructure/GuardianInviteServiceTests.cs`: issuing an invite for an unregistered guardian, no invite issued for an already-registered one, accepting a valid invite creates the paired account and sets `ParentGuardian.AppUserId`, rejecting an expired/already-consumed/tampered token, and role-gating denial on `IssueAsync` for non-Admin/Teacher callers.
