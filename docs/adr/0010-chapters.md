# ADR-0010: Chapters

Status: **Accepted** (built 24/10/2026; decisions by Philip, 24/10/2026, `design/chapters-and-attendance.md` sections 2 and 5). Requirement: `requirements/functional.md` FR-021. Amends `0002` (roles carry a chapter), `0004` and `0006` (a chapter filter alongside the sensitive-data filters), `0008` (applications name a chapter). Assumptions made during the build that are Philip's to confirm are listed at the end.

## Context

VLMS was built for one parish. Varangians is to grow into chapters at Orthodox parishes across the country; Shrewsbury Orthodox Church is the first chapter, and the Greek Orthodox Church of the Holy Transfiguration, Coventry, is added as a second chapter for testing. Each chapter's staff must see only their own chapter's people: students, guardians, consent forms and their medical, dietary and emergency details, DBS status tracking, and registration applications. The curriculum, ranks and rank badges stay national.

On what the system holds (Philip, 24/10/2026): Philip, as maintainer, secures the data; each chapter is responsible for what it enters. The app stores no safeguarding incident reports and no DBS certificates, only DBS status tracking (check date, expiry, certificate number, status) for renewals.

This amends the "tens of users, single parish" assumption in `VISION.md`'s scale target (the scale is now some chapters with tens of users each; one database is still enough, revisit at around 50 chapters, `design/chapters-and-attendance.md` 2.2). `VISION.md` itself is updated by the main session, not here.

## Decision

### 1. One database; every person-level record carries its chapter

A `Chapter` entity (name, place, created date, open flag). Thirteen entities implement `IChapterOwned` and carry a non-null `ChapterId` with a foreign key to `Chapter`: `Student`, `ParentGuardian`, `StudentGuardianLink`, `StudentLessonCompletion`, `Certificate`, `StudentBadge`, `StudentRankProgress`, `ConsentRecord`, `ConsentSensitiveDetails`, `DbsCheck`, `GuardianInvite`, `StudentApplication`, `StudentApplicationSensitiveDetails`. The chapter is **denormalised onto children** (a completion carries its student's chapter) rather than derived through navigations, so each filter is a column comparison and no filter needs to join up the tree, which also rules out filter cycles.

National, not chapter-owned: `Rank`, `Lesson`, `LessonFile`, `LessonTagAssignment`, `LessonChangeProposal`, `RankBadge`, `Chapter`, `SensitiveDataAccessLog` (the audit trail). `AppUser` belongs to no single chapter (a person can hold roles in several). `UserRole` carries a nullable chapter (below). A reflection test fails if a new entity is neither `IChapterOwned` (with the filter) nor on the national list.

The migration `AddChapters` creates Shrewsbury as chapter 1 and assigns every existing row to it (a temporary column default of 1, dropped in the same migration so no later insert can join Shrewsbury by omission).

### 2. Separation by EF Core global query filters, as ADR-0004

Each chapter-owned entity has a **named** query filter, `"Chapter"` (EF Core 10 named filters). The three sensitive entities keep their ADR-0004/0006 filter, renamed `"SensitiveData"`; EF Core applies both, so a row must pass both and neither weakens the other. `IgnoreQueryFilters()` stays banned in `src/`. The chapter filter has three arms:

1. `SeesAllChapters`: only the scheduled job's `SystemCurrentUserContext`; never a signed-in person.
2. the row's chapter is the caller's **active chapter**.
3. the row is the caller's own: their linked children by guardian link (and those children's records), their own student record, their own guardian rows, their own registration applications. For everything but applications, arm 3 applies only while the caller holds no staff role (Teacher, Admin, Safeguarding Officer) in their active chapter, so a Teacher in one chapter whose child is in another does not find that child in their staff lists, or record completions for them; they switch to the child's chapter, where they hold Parent. A parent who is only a parent keeps their children whichever chapter is active (Philip, 24/10/2026). Applications keep arm 3 always (an applicant holds no role in the chapter yet, so cannot switch to it); the staff review lists scope themselves to the active chapter.

Writes are guarded in `VlmsDbContext.SaveChanges` (`Chapters/ChapterWriteRules`): a new top-level record (student, guardian, DBS check) goes into the caller's active chapter, and any other chapter is refused (the system principal may name a chapter); a registration application must name an open chapter (the applicant chooses it); a child record takes its parent's chapter, read through the filters, so a child of another chapter's student is refused; a guardian link, invite or consent record whose guardian is in another chapter is refused; no record changes chapter (a `SetValues` copy that leaves `ChapterId` at 0 keeps the original).

### 3. Roles per chapter, and the active chapter

`UserRole` gains `ChapterId` (null for a national role) and a surrogate key; uniqueness is per (user, role, chapter), and per (user, role) for national roles (two partial unique indexes, because NULLs are distinct in a plain unique index). Chapter roles: Admin, Teacher, Safeguarding Officer, Parent, Student. National: Approver (curriculum only, unchanged) and the new **National Administrator**.

Every signed-in person has an **active chapter**: their last choice with the chapter switcher, if they still hold a role there, otherwise the lowest-numbered chapter they hold a role in. `IdentityCurrentUserContext` works it out on every read, like the roles (FR-016), and `HasRole(role)` for a chapter role means "holds it in the active chapter". Every page policy, in-service role check and query-filter arm reads `HasRole`, so a Teacher in A who is Admin in B has no Admin powers while working in A. A staff leaver's staff rows do not count towards membership (FR-017).

**Where the choice is kept: the database, not a cookie.** The task suggested a cookie validated on every request. Blazor Server interactive pages run on a circuit with no per-request `HttpContext`, so a cookie read at the first request would not change until reconnection, and passing it into the circuit adds moving parts. The choice is stored in `active_chapter_choices` (one row per person) and validated against the person's current roles on every check, which gives the same guarantee (a choice never grants anything; losing the role undoes it) with no new state path. Trade-off: the choice is per person, not per browser, so switching on a phone also switches an open laptop session on its next action; every check stays consistent, and a page loaded in the old chapter simply finds nothing when acted on. The switcher is a plain antiforgery-checked form post (`POST /chapters/switch`, local return paths only), shown in the shell only to someone who belongs to more than one chapter. The shell shows the active chapter's name to signed-in users; public pages stay national ("Varangians", no parish).

### 4. The National Administrator: least privilege

Philip holds it. Powers (`Chapters/ChapterService`, page `/administration/chapters`): create and edit chapters (name, place, open or closed), appoint and remove each chapter's Admins (an account with a confirmed email that has not left; a chapter's last Admin cannot be removed), and manage the national roles (Approver, National Administrator; the last National Administrator cannot be removed). It needs TOTP MFA (`MfaRequirementHandler`, and checked again in the service). It matches no arm of any chapter filter or sensitive-data filter, so it sees **no** chapter's children, consent, DBS status tracking or applications unless the person also holds a role in that chapter. The first one is granted from the server shell, `Vlms.Jobs bootstrap-national-admin <email>`, inert once one exists (same pattern as `bootstrap-admin`, which now grants Admin in Shrewsbury).

National figures (`/reporting/national`, `Chapters/NationalTotalsService`): students on roll per chapter per rank, counts only. Because the National Administrator sees no student through the filters, these are read with one fixed aggregate SQL statement that returns a chapter id, a rank id and a count. Chapter Admins keep their full reports, which the filters now scope to their own chapter.

### 5. Chapter Admins

`RoleAssignmentService` and `/administration/user-roles` work on the Admin's active chapter only: they list the people holding a role in it (and only those roles), grant Admin, Teacher, Safeguarding Officer or Parent there, and revoke them there; the last-Admin guard counts that chapter's Admins. Someone new is found by their exact email address rather than by listing every account on the platform. National roles are not granted here. Leaving (`LeavingService.MarkStaffLeftAsync`) is recorded on the account, so it would end staff roles in every chapter; a chapter Admin may mark as left only staff whose staff roles are all in their own chapter.

### 6. Registration, notifications, the scheduled job

The applicant chooses the chapter from the open chapters; that chapter's Safeguarding Officer reviews and its Admin approves; approval grants Parent in that chapter. Staff emails (registration submitted and passed review, consent submitted, the daily safeguarding escalation digest) go to the affected chapter's staff only; the digest is one email per chapter. The job's DBS sweep treats a teacher at two chapters as one teacher per chapter, each needing a current clear check recorded by that chapter.

### 7. The few sanctioned reads past the filters

All in reviewed, gate-checked places (`build/check-access-control.ps1` fails on raw SQL in any other file, and on any other caller of the scope override):

- `VlmsDbContext.NextIdAsync<T>`: the next application-assigned id is `MAX(id)+1` over the whole table. Through the filter, a chapter would see only its own rows and hand out an id another chapter already uses. Returns one number.
- `NationalTotalsService`: the anonymous counts above.
- `GuardianInviteService`: someone accepting an invite is usually not signed in, so has no active chapter. After the token's Data Protection signature and expiry are checked, the invite's chapter id is read with one scalar query, and `ChapterScopeOverride` makes that chapter the active one for the rest of the accept call. Roles are unchanged, so an anonymous caller still sees no sensitive entity.
- `VlmsUserConfirmation` (at sign-in, before anyone is signed in) asks "is this a student's sign-in?" through a context that is that one account (`AccountLookupUserContext`), so the own-record arm finds that student's row and nothing else.

## Consequences

- Every new person-level entity must implement `IChapterOwned` and get the filter, or the reflection test fails. Every staff list of people holding a role (`UserRole` has no filter, because identity checks must see all of a person's roles) must scope to the chapter explicitly; the existing ones do (`ChapterMembership.StaffIn`).
- Each role check now costs a few small primary-key queries (active chapter plus role). Acceptable at this scale; revisit with a per-request cache only if measured.
- `UserRole`'s key changes; the Down migration cannot restore the old (user, role) key once someone holds the same role in two chapters, which is the honest result.
- The DPIA and privacy notice change with chapters (data responsibility, `design/chapters-and-attendance.md` 5 item 1, is still open).

## Assumptions (least privilege chosen; Philip to confirm)

**Confirmed by Philip, 24/10/2026:** 1, 2, 4, 6 and 8; **27/10/2026:** 3, 5 and 7, and 9 amended (below). The DBS certificate number is kept (Philip, 24/10/2026).

1. **Rank badges are managed by the National Administrator only.** Badges are national; a chapter Admin could otherwise change every chapter's artwork. The National Administrator can also read the lesson library (national curriculum, no child data) so the badges page works.
2. **Approver is granted by the National Administrator**, not by chapter Admins (it is a national role).
3. **Role-less accounts are not listed to chapter Admins.** Before chapters, every account with no role appeared on the role page; now an Admin finds a new person by exact email, and the waiting list shows only those who accepted one of that chapter's guardian invites.
4. **Marking staff as left is refused when they are on another chapter's staff too** (leaving is account-wide); the Admin removes their roles in this chapter instead.
5. **The active chapter is per person, kept in the database** (section 3), not per browser.
6. **Staff see their own children from another chapter only after switching to it** (section 2, arm 3).
7. **National figures show exact counts**, however small; no small-number suppression was asked for. With very small chapters a count of one could point to a child for someone who knows the parish; Philip may want counts below a threshold hidden.
   - **Confirmed (Philip, 27/10/2026):** exact counts, because only the National Administrator (Philip) sees them; revisit if someone outside the charity's trustees holds that role. **Added (Philip, 27/10/2026):** the national figures pages show collated national totals first, at the top, followed by the per-chapter breakdown.
8. **The Shrewsbury chapter is named "Shrewsbury Orthodox Church"** (place "Shrewsbury"); the National Administrator can rename it.
9. **MFA for chapter roles is judged in the active chapter**: a Teacher in A who is only a Parent in B is not asked for MFA while working in B, where they hold no staff role. Their B access is a parent's.
   - **Amended (Philip, 27/10/2026):** an account holding a staff role in any chapter must use MFA whatever its active chapter: one account has one password, and a stolen one must not sign in without MFA as a parent elsewhere.
