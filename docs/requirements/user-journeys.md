# User Journeys

Status: drafted 22/10/2026, **awaiting Philip's review**. The journeys were written after the MVP build and walked against the app as built (commit `4ac9a83`), because the build had gone ahead without them and missed the app's core purpose (`functional.md`, "Lesson content access").

Each step is marked:
- **Built:** a page or service does it, and the reference is given.
- **Partial:** part of it exists. The note says what is missing.
- **Missing:** nothing in the app does it.

"Service only" means the logic exists in `Vlms.Infrastructure` but no page calls it, so no user can reach it.

## J1. Teacher: prepare and deliver the month's session

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Sign in | Built | `Account/Login` |
| 2 | Land on a home page that shows what to do next | Built | `Home.razor` shows a "what to do next" section per held role; `Layout/NavMenu.razor` is the persistent, role-filtered navigation (`NavigationCoverageTests`). |
| 3 | Browse the curriculum by rank, in delivery order, with tags | Built | `/lessons` (`LessonLibraryService`), 05dc741. |
| 4 | Open a lesson's documents and the Teacher's Information to prepare | Built | `GET /lessons/files/{id}`: PDF and images inline, other types downloaded. |
| 5 | Print or download handouts | Built | Same as step 4. |
| 6 | See the students in their group, with each student's current rank | Works (browser walk 22/10/2026) | `/students` (`StudentRosterService`): all active students, an "assigned to me" filter, rank and progress in rank. Each opens `/students/{id}` (completions, badges, certificates, consent status). Certificate links go to `/certificates/{id}`. |
| 7 | Record that each student who attended completed the lesson | Works (browser walk 22/10/2026) | `/progress/record` calls `CompletionService.MarkCompleteBatchAsync` (certificate, promotion and parent email as before). |
| 8 | Check that a student with lapsed consent is blocked from completion | Works (browser walk 22/10/2026) | FR-003, enforced in `CompletionService`; `/progress/record` shows blocked students with the reason, and reports blocked results after submitting. |
| 9 | Correct a mistaken completion | Built | `/progress/completions` (`CompletionReversals.razor`) |

## J2. Teacher: improve a lesson

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Find the lesson and open its current content | Built | `/lessons` and `GET /lessons/files/{id}`. |
| 2 | Propose an edit: change title, order and tags, and upload new or replacement files | Works (browser walk 22/10/2026): edit with an uploaded file; the file reached the library only after approval | `/curriculum/teacher`: title, order and tags (c8c13f4); files are uploaded with `InputFile` (`LessonUploadService`: 25 MB default limit, extension allow-list, content-type and signature checks), start from the lesson's current files, and can be removed or reordered. Uploads are pending and go live only on approval. |
| 3 | Propose a new lesson or the retirement of one | Built | `/curriculum/teacher` |
| 4 | See the Approver's decision and comments, then revise and resubmit | Built | `/curriculum/teacher`, "My proposals" |

## J3. Approver: review a proposed change

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | See pending proposals | Works (browser walk 22/10/2026) | `/curriculum/approver` |
| 2 | Compare the proposed content with the current lesson, opening the files of both | Works (browser walk 22/10/2026) | `/curriculum/approver` shows each proposal as a table of the current lesson against the proposal, differences marked in words (Changed / Added / Removed). Current files open from the library endpoint; proposed (pending) files open from `GET /curriculum/proposals/{id}/files/{index}`, for the proposer and Approvers only. |
| 3 | Approve, or reject with comments | Works (browser walk 22/10/2026) | `/curriculum/approver`. Approval puts the change live (`LessonProposalService.ApproveAsync`). |

## J4. Student: learn and track progress

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Get an account and sign in | Works (browser walk 22/10/2026) | A linked parent creates the username and password at `/parent/student-sign-ins` (linked from `/parent/dashboard`) and can reset it; an Admin can reset at `/administration/student-sign-ins`. The student signs in at `/Account/Login` with the username (FR-011, `adr/0007-student-sign-in-accounts.md`, `StudentAccountService`). Student passwords need only 8 characters; the student stays signed in on their own device for 90 days between visits (`VlmsSignInManager`); a parent's or Admin's reset clears a lockout. An Admin can turn the sign-in off and on again at `/administration/student-sign-ins`: a turned-off sign-in can't log in, open sessions end within 30 minutes, and a parent's reset doesn't turn it back on. Leaving doesn't turn it off. |
| 2 | Browse all lessons in every rank and open or download them | Works (browser walk 22/10/2026) | `/lessons` admits the Student role; linked from `/student`. |
| 3 | See own rank, completed lessons, badges and certificates | Works (browser walk 22/10/2026) | `/student` (`StudentPageService`): own record only, resolved from the sign-in, no id in the URL. Badge images from `/badges/{rankId}/image`. |
| 4 | Download own certificates | Works (browser walk 22/10/2026) | `/student` links View and Download for each certificate to `GET /certificates/{id}` (inline, or `?download=1`). |

## J5. Parent: follow and support their child

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Accept an invite, register, and be approved by an Admin as Parent | Built (fix for the browser-walk defect, 22/10/2026; not yet rechecked in the running app). A new email sets a password as before. An email that already has an account (a Teacher who is also a parent) is asked to sign in, returns to the invite after sign-in (including two-factor) and accepts, which links that account; any other signed-in account is refused. Only one working invite per guardian: a further child linked before the invite is used sends a note pointing to it, and a guardian who already has an account gets a "child linked" note instead of an invite | `/guardianship/accept-invite`, `/administration/user-roles` (the waiting list now also shows a guardian who already holds other roles; approving adds Parent to them). FR-004, TC-031 |
| 2 | See each linked child's rank, completions, badges and consent status | Built | `/parent/dashboard` |
| 3 | Download a child's certificates | Built | `/parent/dashboard` links View and Download for each certificate to `/certificates/{id}`. |
| 4 | Browse and open all lessons to support learning at home | Built | `/lessons` (Parent role). |
| 5 | Receive emails on completion, promotion and consent expiry | Built (untested live) | `GuardianNotificationService`. The first real send waits for go-live. |
| 6 | Submit or renew the annual consent form | Works (browser walk 22/10/2026): submit, approve, update and re-approve; the approved form stays in force while the update is pending | `/parent/consent`; Safeguarding Officer or Admin decides on `/safeguarding/consent` (FR-015, ADR-0006), 6f5b5d7. The review marks a changed name or date of birth, and any field that differs from the consent in force, as "Changed" (TC-031). |
| 7 | Create and reset a sign-in for each of their children | Works (browser walk 22/10/2026): one parent, two children, a sign-in each; 8-character rule enforced | One parent account serves every linked child. `/parent/student-sign-ins` lists each linked child with Create sign-in (no sign-in yet) or Reset password (has one), and shows when the school has turned a child's sign-in off (FR-011, `adr/0007`). Tested with a parent of three children. |
| 8 | Change their own password while signed in | Works (checked in the running app 22/10/2026: parent changed password, wrong current password refused, changed back) | `/Account/Manage/ChangePassword` (linked from the header once signed in): current password, new password, confirmation; the adult password rule applies and the session stays signed in. Together with step 7 this covers Philip's decision (22/10/2026) that a parent can reset both their own password and each child's. The same page serves every adult role (Admin, Teacher, Approver, Safeguarding Officer); a student is told to ask their parent (FR-011, `adr/0007` decision 1, TC-030). |

## J6. Admin: run the programme

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Bootstrap the first Admin, assign roles, approve parents | Built | `Vlms.Jobs bootstrap-admin`, `/administration/user-roles`. A role change takes effect on the user's next action, including in pages already open, and rotates their security stamp (FR-016, `RoleChangeTests`); not yet checked in the running app. |
| 2 | Register a student and link guardians | Works (browser walk 22/10/2026) after fix 1d674e8 (registration failed on PostgreSQL: non-UTC date) | `/registration/students`, `/guardianship/links` |
| 3 | Edit a student (name, assigned teacher, status) | Built | `/administration/students` (Admin: assigned teacher and status). Name and date of birth come from the approved consent form (FR-013), a28a6ac. |
| 4 | Load the curriculum (ranks, lessons, files) | Built | `Vlms.Jobs seed-curriculum` (`deploy/README.md` §6b); run against local PostgreSQL 22/10/2026: 10 ranks, 56 lessons, 314 files. |
| 5 | Set rank badge images | Works (browser walk 22/10/2026) | `/administration/rank-badges` (`RankBadgeService`). Images are served from `/badges/{rankId}/image` and shown on `/parent/dashboard`. Admin-only upload: PNG, JPEG, WebP or SVG, up to 2 MB. One badge per rank, enforced by a unique index (TC-028). |
| 6 | View progress reports and at-risk students | Built | `/reporting` |
| 7 | Handle leavers and retention | Partial | `/retention/mark-as-left` and `/reporting/records-due-for-deletion` exist. Purge waits on decision 2. |

## J7. Safeguarding Officer: keep consent and DBS current

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Record and approve consent | Built | `/safeguarding/consent` |
| 2 | Record DBS checks | Built | `/safeguarding/dbs` |
| 3 | Receive expiry alerts | Built (untested live) | `ConsentExpiryJob` and notifications |

## Gap summary (for Philip)

Core gaps (the app can't be used for its purpose without them):
1. **Lesson content access**: browse, open and download for all roles; upload through proposals; Approver opens files. Already queued.
2. **Record lesson completion**: a teacher page to mark students complete (J1 step 7). The service exists; no page calls it.
3. **Certificate download** for students and parents (J4 step 4, J5 step 3). The PDFs are generated but can't be read back.
4. **Student experience**: student accounts, plus a student page for progress, badges, certificates and lessons (J4).
5. **Teacher's student list** with current ranks (J1 step 6).
6. **Home page and navigation** per role (J1 step 2).

Needed but smaller:
7. Edit student details (J6 step 3).
9. ~~Rank badge images (J6 step 5)~~: built, see J6 step 5.
8. ~~Approver view that shows the proposal readably, compared with the current lesson (J3 step 2).~~ Built with FR-007.

Decided (Philip, 22/10/2026):
10. Parents submit the consent form in the app, and the Safeguarding Officer or Admin approves (FR-015).

All ten gaps are requirements FR-006 to FR-015 in `functional.md` and are queued in `STATE.md`. Update each step's status here as its item lands.

Further decisions (Philip, 22/10/2026): students sign in with a username and password created by their parent (FR-011); student details come from the consent form, and only Admins assign teachers and change status (FR-013); parents view and update, and teachers view, consent details (FR-015); certificates are viewable and downloadable, with designs to come later (FR-010).
