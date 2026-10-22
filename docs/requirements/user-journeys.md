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
| 2 | Land on a home page that shows what to do next | **Missing** | `Home.razor` is still the template ("Hello, world!") plus a list of links. There is no navigation menu. |
| 3 | Browse the curriculum by rank, in delivery order, with tags | **Missing** | The only lesson list is on `/curriculum/teacher`, which is for proposing changes. |
| 4 | Open a lesson's documents and the Teacher's Information to prepare | **Missing** | No page serves lesson files, and `IBlobStorage` has no read method (`UploadAsync` only). |
| 5 | Print or download handouts | **Missing** | Same as step 4. |
| 6 | See the students in their group, with each student's current rank | **Missing** | No student list page exists for teachers. Students appear only in admin, reporting and guardian-link pages. |
| 7 | Record that each student who attended completed the lesson | Built (not yet linked) | `/progress/record` calls `CompletionService.MarkCompleteBatchAsync` (certificate, promotion and parent email as before). Not reachable from navigation or the home page until FR-012. |
| 8 | Check that a student with lapsed consent is blocked from completion | Built | FR-003, enforced in `CompletionService`; `/progress/record` shows blocked students with the reason, and reports blocked results after submitting. |
| 9 | Correct a mistaken completion | Built | `/progress/completions` (`CompletionReversals.razor`) |

## J2. Teacher: improve a lesson

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Find the lesson and open its current content | **Missing** | Same as J1 step 4. |
| 2 | Propose an edit: change title, order and tags, and upload new or replacement files | **Partial** | `/curriculum/teacher` handles title, order and tags (c8c13f4). Files are typed in as storage keys, and there is no upload. |
| 3 | Propose a new lesson or the retirement of one | Built (except files) | `/curriculum/teacher` |
| 4 | See the Approver's decision and comments, then revise and resubmit | Built | `/curriculum/teacher`, "My proposals" |

## J3. Approver: review a proposed change

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | See pending proposals | Built | `/curriculum/approver` |
| 2 | Compare the proposed content with the current lesson, opening the files of both | **Partial** | The proposal is shown as raw JSON (`<pre>@proposal.ProposedContent</pre>`). Files can't be opened, and there is no comparison with the current lesson. |
| 3 | Approve, or reject with comments | Built | `/curriculum/approver`. Approval puts the change live (`LessonProposalService.ApproveAsync`). |

## J4. Student: learn and track progress

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Get an account and sign in | **Partial** | `Student` role and `Student.AppUserId` exist, but there is no student invite or account-linking flow. |
| 2 | Browse all lessons in every rank and open or download them | **Missing** | Same as J1 step 4. Scope: all ranks (Philip, 22/10/2026). |
| 3 | See own rank, completed lessons, badges and certificates | **Missing** | No student page exists. `StudentSelfAccessHandler` authorises it, but nothing uses it. |
| 4 | Download own certificates | **Missing** | Certificate PDFs are written but can't be read back (no read in `IBlobStorage`, no endpoint). |

## J5. Parent: follow and support their child

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Accept an invite, register, and be approved by an Admin as Parent | Built | `/guardianship/accept-invite`, `/administration/user-roles` |
| 2 | See each linked child's rank, completions, badges and consent status | Built | `/parent/dashboard` |
| 3 | Download a child's certificates | **Missing** | Certificates are listed as text only. Same read gap as J4 step 4. |
| 4 | Browse and open all lessons to support learning at home | **Missing** | Same as J1 step 4. Scope: all ranks (Philip, 22/10/2026). |
| 5 | Receive emails on completion, promotion and consent expiry | Built (untested live) | `GuardianNotificationService`. The first real send waits for go-live. |
| 6 | Submit or renew the annual consent form | **Missing** | Consent is entered by an Admin or Safeguarding Officer "on behalf of" the parent (`/safeguarding/consent`). Decided 22/10/2026: parents complete and submit it in the app, and the Safeguarding Officer or Admin approves (FR-015). |

## J6. Admin: run the programme

| # | Step | Status | Evidence / gap |
|---|---|---|---|
| 1 | Bootstrap the first Admin, assign roles, approve parents | Built | `Vlms.Jobs bootstrap-admin`, `/administration/user-roles` |
| 2 | Register a student and link guardians | Built | `/registration/students`, `/guardianship/links` |
| 3 | Edit a student (name, assigned teacher, status) | **Missing** | No student edit page or service. Fields are set only at registration, by promotion or by "mark as left". |
| 4 | Load the curriculum (ranks, lessons, files) | **Missing** | The seed item is queued in STATE.md. |
| 5 | Set rank badge images | **Missing** | `RankBadge.ImageBlobKey` exists, but nothing creates badges. |
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
8. Approver view that shows the proposal readably, compared with the current lesson (J3 step 2).
9. Rank badge images (J6 step 5).

Decided (Philip, 22/10/2026):
10. Parents submit the consent form in the app, and the Safeguarding Officer or Admin approves (FR-015).

All ten gaps are requirements FR-006 to FR-015 in `functional.md` and are queued in `STATE.md`. Update each step's status here as its item lands.

Further decisions (Philip, 22/10/2026): students sign in with a username and password created by their parent (FR-011); student details come from the consent form, and only Admins assign teachers and change status (FR-013); parents view and update, and teachers view, consent details (FR-015); certificates are viewable and downloadable, with designs to come later (FR-010).
