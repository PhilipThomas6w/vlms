# Chapters (FR-021, adr/0010)

Read `docs/adr/0010-chapters.md` first; this page maps it to the code.

## Data

- `Vlms.Domain/Chapter.cs` (Shrewsbury is `Chapter.ShrewsburyId` = 1, seeded by the model's `HasData` and by migration `20261024084910_AddChapters`), `IChapterOwned.cs`, `ActiveChapterChoice.cs`.
- 13 entities implement `IChapterOwned` (`ChapterId`, non-null, FK `Restrict`, indexed): Student, ParentGuardian, StudentGuardianLink, StudentLessonCompletion, Certificate, StudentBadge, StudentRankProgress, ConsentRecord, ConsentSensitiveDetails, DbsCheck, GuardianInvite, StudentApplication, StudentApplicationSensitiveDetails. Children carry their parent's chapter (denormalised).
- `UserRole` now has a surrogate `Id` and nullable `ChapterId` (null = national role); partial unique indexes `IX_user_roles_user_id_role_chapter_id` / `IX_user_roles_user_id_role_national`. `Role.NationalAdministrator` appended (value 6); `RoleScope.IsChapterRole/IsNationalRole`.

## Read side: `VlmsDbContext.ConfigureChapters`

Named filter `VlmsDbContext.ChapterFilter` ("Chapter") on each chapter-owned entity; the sensitive filters are now named `SensitiveDataFilter` ("SensitiveData") and both apply. Arms: `SeesAllChapters` (system job only) | row in `ActiveChapterId` | the caller's own rows (`OwnRecordsArmApplies`: resolved caller holding no staff role in the active chapter; applications use plain `UserId != null`).

## Write side: `Chapters/ChapterWriteRules` (called from `SaveChanges` overrides)

Top-level rows default to / must be in the active chapter (system principal may name one); applications must name a chapter; children take the parent's chapter (looked up through the filters, tracked entities first); guardian/student chapter mismatch refused; `ChapterId` never changes (0 from `SetValues` restores the original); chapter roles get the active chapter if null, national roles must have none.

## Who you are working as

- `ICurrentUserContext.ActiveChapterId` / `SeesAllChapters` (new members; every implementation updated: Identity, System (sees all, no active chapter), Null, `Security/AccountLookupUserContext` (one account, for `VlmsUserConfirmation` at sign-in), tests' `FakeCurrentUserContext` (defaults to Shrewsbury) and `TestSeedUserContext`).
- `IdentityCurrentUserContext`: active chapter = `Chapters/ChapterMembership.ResolveActiveChapter` (valid `ActiveChapterChoice`, else lowest chapter with a counting role); chapter roles count only there; re-read every call. `Chapters/ChapterScopeOverride` (scoped) overrides the active chapter only inside `GuardianInviteService`'s token-verified accept calls.
- Switcher: `Components/Layout/ChapterBar.razor` in `MainLayout` (name always, switch form only with >1 chapter) posting to `ChapterSwitchEndpoint` (`POST /chapters/switch`, `RequireSignedIn`, antiforgery, local return paths).

## Services and pages

- `Chapters/ChapterService`: open chapters (applicants), my chapters / switch, and the National Administrator's chapters, chapter Admins and national roles (MFA checked in the service). Page `/administration/chapters` (`RequireNationalAdministrator`).
- `Chapters/NationalTotalsService` + `/reporting/national`: counts per chapter per rank via one fixed aggregate SQL statement.
- `RoleAssignmentService` per active chapter; `FindByEmailAsync` for people with no role there; waiting list = this chapter's invite accepters. `LeavingService.MarkStaffLeftAsync` refuses staff of another chapter.
- `StudentApplicationService.SubmitAsync(chapterId, …)`; staff lists via `StaffApplications` (active chapter). `Apply.razor` has the chapter select.
- Recipients per chapter: `ChapterMembership.StaffIn` in `RegistrationNotificationService`, `ConsentNotificationService`, `ConsentExpiryNotifier` (one digest per chapter). `ConsentExpiryFlag`/`DbsExpiryFlag` carry `ChapterId`; the DBS sweep is per (teacher, chapter).
- Rank badges are managed by the National Administrator only; `LessonLibraryService.LibraryRoles` includes it.
- Ids: `VlmsDbContext.NextIdAsync<T>` (whole-table `MAX(id)+1`) replaces `Set<T>().Max()` for chapter-owned entities.
- `Vlms.Jobs`: `bootstrap-national-admin <email>`; `seed-dev-chapters --password <pw>` (Development only, `Chapters/DevelopmentChapterSeeder`: Coventry with Admin, Teacher, Safeguarding Officer, parent, student; accounts `coventry.*@example.test`, student username `coventry.student`).

## Gate

`build/check-access-control.ps1`: raw SQL (`SqlQuery*`, `FromSql*`, `ExecuteSql*`) only in `VlmsDbContext.cs`, `NationalTotalsService.cs`, `GuardianInviteService.cs`; `EnterForVerifiedInvite` only in `GuardianInviteService.cs`; `ChapterSeparationTests.cs` floor 26; chapter sources hashed.

## Tests

`tests/Vlms.Tests/Infrastructure/ChapterSeparationTests.cs` (reflection classification, per-entity theories, own children, write rules, roles per chapter, Admin and national powers, notifications), `DevelopmentChapterSeederTests.cs`.
