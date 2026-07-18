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

## Authorization: Admin/Teacher only, checked at both layers

Role-checked inside `StudentRegistrationService` itself (defense in depth, same pattern as `GuardianLinkService`/`LessonProposalService`/`CompletionService`) — Admin or Teacher only. This is the same hard constraint that governs `StudentGuardianLink` (CLAUDE.md Project Law): the `Student` record that anchors a guardian link is never created by parent self-service either. Because `GuardianLinkService`'s own entry points also independently role-check, a caller reaching either service directly by any path is denied at both layers, not just one.

## Page

`src/Vlms.Web/Components/Pages/Registration/RegisterStudent.razor` (`/registration/students`), gated by `[Authorize(Policy = "RequireAdminOrTeacher")]` — no ad hoc role checks, same convention as `GuardianLinks.razor`. Reuses the same "link existing guardian vs register a new one" form shape as `GuardianLinks.razor`. Linked from `Home.razor` via `<AuthorizeView Policy="RequireAdminOrTeacher">`, above the existing guardian-links link.

## Tests

`tests/Vlms.Tests/Infrastructure/StudentRegistrationServiceTests.cs` (SQLite-in-memory-via-DI pattern, same as `GuardianLinkServiceTests`): both entry points by Admin and by Teacher (asserting the `Student` row, the `StudentRankProgress` row's exact shape, and the guardian link/`ParentGuardian` all exist); denial for Parent and Approver with an explicit assertion that no `Student`/`StudentRankProgress`/`ParentGuardian` row is created; blank-name rejection; no-Rank-reference-data rejection; and the `PromotionService` integration test described above.

## Scope note

Does not build a `Student` edit/deactivation flow, duplicate-matching on Name+DOB (data-design.md names this as a registration hypothesis to confirm at build time — not addressed here, carried forward if it turns out to matter), or an `AssignedTeacherUserId` picker in the page (the service accepts it; the page currently always passes `null`, leaving that assignment as a later Admin action rather than blocking this item's scope).
