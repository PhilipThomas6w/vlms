# Student registration/enrolment

Design source: `docs/design/data-design.md` `Student`/`StudentRankProgress` entities, `docs/requirements/functional.md` FR-004, STATE.md. Closes the precondition `PromotionService`'s doc comment flags (see [progress-tracking.md](progress-tracking.md)): nothing before this increment created a `Student` record or opened their first `StudentRankProgress` row, so `PromotionService.CheckAndPromoteAsync` had no student to promote in the first place.

`StudentRegistrationService` (`src/Vlms.Infrastructure/Registration/StudentRegistrationService.cs`) does, in one operation: creates the `Student` row, opens the first `StudentRankProgress` row for their starting rank, and creates a `StudentGuardianLink` by calling into the existing `GuardianLinkService` (see [guardian-links.md](guardian-links.md)) — not duplicating its logic.

## Starting rank: smallest `Rank.Order`

data-design.md documents `Rank.Order` as already fully describing ladder position (the same fact `PromotionService` relies on for "next rank"), but doesn't explicitly name which rank a brand-new student starts at. Build-time judgement call, consistent with the existing Order-based model: the starting rank is the `Rank` with the smallest `Order` — the bottom of the ladder. If no `Rank` reference data exists at all, registration throws `InvalidOperationException` rather than fabricating one (same "reference data is populated separately" precondition as `PromotionService`'s `RankBadge` lookup).

## The `StudentRankProgress` row this opens

`RankId` = the starting rank's Id (same as the new `Student.CurrentRankId`), `StartedAt` = the enrolment date (as a `DateTime`), `CompletedAt` = `null`. This is exactly the shape `PromotionService.CheckAndPromoteAsync` looks up (`StudentId == studentId && RankId == currentRank.Id && CompletedAt == null`) — a dedicated integration test (`StudentRegistrationServiceTests.RegisterThenComplete_DrivesThroughPromotionService_WithoutThrowing`) registers a student, completes their one active lesson, and drives them through `PromotionService.CheckAndPromoteAsync` successfully, proving the precondition is actually satisfied rather than just structurally plausible.

## Two entry points, mirroring `GuardianLinkService`

- **`RegisterStudentWithNewGuardianAsync(name, dateOfBirth, enrolmentDate, assignedTeacherUserId, guardianName, guardianContactInfo, guardianIsPrimary)`** — the common case: no `ParentGuardian` record exists yet, so one is created and linked in the same operation (calls `GuardianLinkService.RegisterGuardianAndLinkAsync`).
- **`RegisterStudentWithExistingGuardianAsync(name, dateOfBirth, enrolmentDate, assignedTeacherUserId, parentGuardianId)`** — links an already-known guardian, e.g. enrolling a second child of a family already in the system (calls `GuardianLinkService.CreateLinkAsync`).

Both take a `GuardianLinkService` via constructor injection (registered `AddScoped` in `Program.cs`, so DI supplies the same-scope instance sharing the same `VlmsDbContext`) rather than constructing one internally — cleaner for both production DI and tests, which can pass in their own `new GuardianLinkService(context, currentUser)` sharing the same context.

## Atomicity: both entry points run inside one explicit EF Core transaction (commit `d3a7e0d`, load-bearing)

This is not incidental — it's the property a checker round-trip found missing and fixed, and it must be preserved by any future change to either entry point. The original version (commit `b85cf38`) called `SaveChangesAsync` once to commit the new `Student` + its `StudentRankProgress` row, then separately called into `GuardianLinkService` (its own `SaveChangesAsync`) for the guardian link — two independent commits, not one operation. If the guardian step then threw (a blank guardian name, or an unknown `parentGuardianId` in `RegisterStudentWithExistingGuardianAsync`), the `Student` and its open `StudentRankProgress` row were already permanently persisted with **no** guardian link at all — silently contradicting this page's own "does, in one operation" framing above, and (per the checker) letting `RegisterStudent.razor`'s retry-on-error UI create a second orphaned `Student` on resubmission, since nothing here does duplicate-matching on name/DOB.

**The fix, and why it works:** both `RegisterStudentWithNewGuardianAsync` and `RegisterStudentWithExistingGuardianAsync` now wrap the student-creation call and the `GuardianLinkService` call in a single `await using var transaction = await _db.Database.BeginTransactionAsync(ct)` / `await transaction.CommitAsync(ct)`. This only works because `StudentRegistrationService` and `GuardianLinkService` share the same DI-scoped `VlmsDbContext` (see "Two entry points" above) — both services' `SaveChangesAsync` calls enlist in the one ambient transaction rather than each committing independently. An unhandled throw from either step leaves the transaction uncommitted; it rolls back on `Dispose` (the `await using`), so **zero** rows survive — no orphaned `Student`, no orphaned `StudentRankProgress`, no partial guardian state.

Proven, not just asserted: two dedicated tests (`StudentRegistrationServiceTests`) drive each failure mode — a blank guardian name, and an unknown `parentGuardianId` — and each asserts the `Students`, `StudentRankProgresses`, `StudentGuardianLinks`, and `ParentGuardians` tables are all empty afterward, not merely that the throw itself happened. Treat "wraps guardian-link creation" (above) and "atomic across both steps, zero partial rows on any failure" as one and the same guarantee when changing this service — don't reintroduce two independent `SaveChangesAsync` calls without re-establishing the transaction around them.

## Authorization: Admin/Teacher only, checked at both layers

Role-checked inside `StudentRegistrationService` itself (defense in depth, same pattern as `GuardianLinkService`/`LessonProposalService`/`CompletionService`) — Admin or Teacher only. This is the same hard constraint that governs `StudentGuardianLink` (CLAUDE.md Project Law): the `Student` record that anchors a guardian link is never created by parent self-service either. Because `GuardianLinkService`'s own entry points also independently role-check, a caller reaching either service directly by any path is denied at both layers, not just one.

## Page

`src/Vlms.Web/Components/Pages/Registration/RegisterStudent.razor` (`/registration/students`), gated by `[Authorize(Policy = "RequireAdminOrTeacher")]` — no ad hoc role checks, same convention as `GuardianLinks.razor`. Reuses the same "link existing guardian vs register a new one" form shape as `GuardianLinks.razor`. Linked from `Home.razor` via `<AuthorizeView Policy="RequireAdminOrTeacher">`, above the existing guardian-links link.

## Tests

`tests/Vlms.Tests/Infrastructure/StudentRegistrationServiceTests.cs` (SQLite-in-memory-via-DI pattern, same as `GuardianLinkServiceTests`): both entry points by Admin and by Teacher (asserting the `Student` row, the `StudentRankProgress` row's exact shape, and the guardian link/`ParentGuardian` all exist); denial for Parent and Approver with an explicit assertion that no `Student`/`StudentRankProgress`/`ParentGuardian` row is created (both denial cases assert the same full set of empty collections, not just the `Student` table); blank-name rejection; no-Rank-reference-data rejection; the `PromotionService` integration test described above; and the two atomicity tests from the `d3a7e0d` fix — blank-guardian-name and unknown-`parentGuardianId` failures, each asserting zero `Students`/`StudentRankProgresses`/`StudentGuardianLinks`/`ParentGuardians` rows survive the rollback.

## Scope note

Does not build a `Student` edit/deactivation flow, duplicate-matching on Name+DOB (data-design.md names this as a registration hypothesis to confirm at build time — not addressed here, carried forward if it turns out to matter), or an `AssignedTeacherUserId` picker in the page (the service accepts it; the page currently always passes `null`, leaving that assignment as a later Admin action rather than blocking this item's scope).
