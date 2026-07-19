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

## Authorization: a new `AnyRoleRequirement`/`AnyRoleAuthorizationHandler`

The guardian-links page needs "Admin OR Teacher", which the existing single-role machinery can't express: `Program.cs`'s `foreach (var role in Enum.GetValues<Role>())` loop wires exactly one `RequireX` policy per role, each requiring that one role. Added `AnyRoleRequirement`/`AnyRoleAuthorizationHandler` (`src/Vlms.Infrastructure/Authorization/`) — additive, not a replacement for `RoleRequirement`/`RoleAuthorizationHandler`, which are unchanged. `Program.cs` wires one new policy, `"RequireAdminOrTeacher"`, from it.

## Page

`src/Vlms.Web/Components/Pages/Guardianship/GuardianLinks.razor` (`/guardianship/links`), gated by `[Authorize(Policy = "RequireAdminOrTeacher")]` — no ad hoc role checks at the page level, same convention as the curriculum pages. Lists existing links, and a form to either link an existing guardian or register a new one, matching the service's two entry points. Linked from `Home.razor` via `<AuthorizeView Policy="RequireAdminOrTeacher">`.

## Scope note (deliberate, at the time this page was built)

This service/page implemented only the guardian-link creation flow FR-004 asks for — not `Student` creation or full `ParentGuardian` CRUD/registration. In particular it did **not** open a `Student`'s first `StudentRankProgress` row — `PromotionService`'s doc comment (see [progress-tracking.md](progress-tracking.md)) had flagged that precondition as expected to land with "student registration". That gap is now closed by [student-registration.md](student-registration.md)'s `StudentRegistrationService`, which reuses this service's two entry points (`RegisterGuardianAndLinkAsync`/`CreateLinkAsync`) rather than duplicating them — this page (`GuardianLinks.razor`) still exists in its own right for linking a guardian to an already-registered `Student` (e.g. correcting/adding a link after the fact).

## Tests

`tests/Vlms.Tests/Infrastructure/GuardianLinkServiceTests.cs` (SQLite-in-memory-via-DI pattern, same as `LessonProposalServiceTests`): both entry points, by Admin and by Teacher; denial for Parent and Approver (with a dedicated assertion that a Parent's attempt creates no row — the hard "never parent self-service" constraint); duplicate-link rejection; the same guardian linking to two different students (not a duplicate — composite key is per-student); unknown-student/unknown-guardian rejection; blank guardian name rejection.

`tests/Vlms.Tests/Authorization/AnyRoleAuthorizationHandlerTests.cs`: succeeds on either listed role, fails when the caller holds neither, and a constructor guard against an empty role list.
