# UI/UX redesign: discovery and audit

Status: **in progress**, 23/10/2026. Phase 1 (discovery) and the first part of phase 2 (audit) of `docs/design/redesign-brief.md`.

- **Done:** source-level review of every routable page, the layout, navigation and CSS. Browser inspection of the public pages at 1280 px and 375 px.
- **Not yet done:** browser inspection of signed-in pages under each role. Blocked on test-account access; see "Blocker" below.

## Blocker: signed-in pages

Signed-in pages need a working account per role, and Teacher, Admin and Safeguarding Officer also need two-factor codes. The local test accounts (`*@vlms.test`, `alex.walk`, `sam.walk`) have no recorded passwords. Claude's attempts to set known passwords in the local database, and to reuse the end-to-end test host's throwaway accounts for screenshots, were refused by the session's permission classifier as credential handling. Options are for Philip to choose.

## What the application is today

- **Styling:** none to speak of. `wwwroot/app.css` is 50 lines: validation outlines, the error summary, `.review-changed`, `.visually-hidden`. Bootstrap was removed, and three leftover `.form-floating` rules still reference it. Every page renders in browser defaults: Times, blue underlined links, 8 px body margin, unstyled native controls. Screenshots: `artifacts/ui/audit/` (not committed).
- **Shell:** `MainLayout.razor` has a header line ("Signed in as … — Two-factor authentication — Change password — Log out"), a flat `NavMenu` list, and `<main>`. There is no footer, no logo, no product name in the header, no `<title>` suffix, and no favicon (`/favicon.ico` 404s). `theme-color` is `#1f2937`; `icons/icon.svg` is a placeholder.
- **Pages:** 41 routes (12 account, 29 app), listed below. 30 tables, about 25 forms, almost no shared components: only `FormErrorSummary`, `ApplicationSummary` and `PendingWorkNotices`.
- **JavaScript:** Blazor plus a service-worker registration. Nothing else.

### Route inventory

| Area | Route | Audience | Character | Notes from the source |
|---|---|---|---|---|
| Account | `/Account/Login`, `LoginWith2fa`, `LoginWithRecoveryCode`, `Register`, `ConfirmEmail`, `ForgotPassword`, `ResetPassword`, `ResendEmailConfirmation`, `AccessDenied`, `Manage/ChangePassword`, `Manage/EnableAuthenticator`, `Manage/TwoFactorAuthentication` | Everyone | Form | Template pages with their own wording. Login mixes "Email or student username": one field for adults and children. |
| Home | `/` | Everyone | Hub | One `<h1>` "Varangians LMS"; a list of links per role held; `PendingWorkNotices` counts. An Admin+Teacher sees about 20 links with repeats (Lesson library appears up to four times, Consent records and DBS checks twice). |
| Lessons | `/lessons` | All roles | Content | One long page: 10 ranks × table (Code, Title, Tags, Files). 56 lessons, 314 file links, no search, no filter, no lesson page. Files open or download. |
| Student | `/student` | Student | Progress | Text and lists: rank name, "X of Y lessons", a native `<progress>`, completed lessons, badges (48 px), certificates. No view of other ranks, no sense of journey, no "next lesson". **Prototype target.** |
| Parent | `/parent/dashboard`, `/parent/consent`, `/parent/student-sign-ins` | Parent | Hub, long form, form | Dashboard per child: rank, consent status, badges, certificates; **no progress within the rank and no completed lessons**, although parents are told it shows progress. |
| Registration | `/registration/apply`, `/registration/my-applications`, `/registration/review`, `/registration/approve`, `/registration/students` | Parent, SO, Admin | Long form, review queue | Apply is a single long form per child, with full consent. Shown in the navigation to **every signed-in user** ("Register a child", "My applications"), including staff and students. |
| Teaching | `/students`, `/students/{id}`, `/students/consent`, `/students/{id}/consent`, `/progress/record`, `/progress/completions` | Teacher, Admin | Tables, task form | Record completions: native `<select>` of 56 lessons grouped by rank, checkbox list, free-text note, results list. **Prototype target.** |
| Curriculum | `/curriculum/teacher`, `/curriculum/approver` | Teacher, Approver | Form, review | Teacher page is 494 lines: proposal form, files, my proposals, all on one page. |
| Safeguarding | `/safeguarding/consent`, `/safeguarding/dbs` | SO, Admin | Tables, review | Consent records page has 3 tables and a record-on-behalf form on one page. |
| Administration | `/administration/students`, `/user-roles`, `/student-sign-ins`, `/rank-badges`, `/guardianship/links`, `/guardianship/accept-invite`, `/retention/mark-as-left` | Admin | Tables with inline actions | Wide tables with per-row buttons; will overflow on phones. |
| Reporting | `/reporting`, `/reporting/records-due-for-deletion` | Admin | Data | 3 tables each, no visualisation. |

### Data available for progression

`StudentRecord` (`Vlms.Infrastructure/Engagement/StudentRosterService.cs`) carries the current rank name, completed and total lessons in the current rank, completions, badges and certificates. It does **not** carry the full rank ladder, lessons remaining in the current rank, or future ranks. A rank-journey view needs a small read-only extension (ranks with lesson counts and titles; no sensitive data). `LessonLibraryService` already loads ranks with ordered lessons and tags.

## Audit findings (prioritised by impact)

Severity: **High** (blocks the brief's goals or fails users), **Medium** (noticeable friction), **Low** (polish).

### Identity and visual design

1. **High: no visual identity at all.** Browser defaults throughout; no brand, logo, colour, type or icon. Nothing tells a child or parent that this is Orthodox, or Varangians.
2. **High: "LMS" framing.** The product calls itself "Varangians LMS" and "learning management system" on the home page. That is institutional language; the brief asks for belonging ("becoming a Varangian").
3. **Medium: no favicon and a placeholder app icon**, which matter for the PWA and the planned Play Store listing.

### Navigation and information architecture

4. **High: flat, role-concatenated navigation.** `NavMenu` emits every link for every role held, in one wrapping row. An Admin who is also a Teacher gets about 20 items. No grouping, no hierarchy, no mobile pattern.
5. **High: the home page lists what you *can* do, not what to do *next*** (FR-012 intended the latter). Only `PendingWorkNotices` is task-oriented. Duplicated links across role sections.
6. **Medium: registration links shown to everyone signed in**, including students and staff, because the policy is `RequireSignedIn`. Noise for most users; confusing for a child.
7. **Medium: account links crammed into a sentence in the header** ("Signed in as … — … — …"). No account menu.

### Student experience

8. **High: progression is invisible.** One rank name, a counter and a native progress bar. No ladder, no past or future ranks, no "next lesson", no celebration of promotion, badges at 48 px in a bullet list. This is the brief's central gap.
9. **Medium: no route from progress to content.** "Open the lesson library" dumps the student into all 56 lessons rather than their current rank's next lesson.

### Lessons and content

10. **High: the lesson library is a file index, not a curriculum.** Ten tables of codes, titles, tags and filenames. No lesson page, so there is nowhere for in-app lesson content to live later. The design must introduce a lesson-level page (for example `/lessons/{code}`) even while content is still files.
11. **Medium: filenames as the primary content label** (Word document names), no file type or size, and downloads mixed with in-browser views.

### Staff workflows

12. **Medium: Record completions** works but is plain: the 56-lesson `<select>` has no "this session's lesson" shortcut and no rank filter; the student list shows no progress context; blocked students are explained only in a trailing sentence; there is no select-all or count; results appear below and are easy to miss on a phone.
13. **Medium: one-page-does-everything staff screens** (`TeacherProposals` 494 lines; `ConsentRecords` 3 tables and a form). Hard to scan; long scrolling on phones.
14. **Medium: wide tables with inline buttons** across administration and safeguarding will overflow at 375 px. No responsive table pattern exists.

### Parent experience

15. **Medium: the parent dashboard shows no progress within the rank and no completed lessons**, which is the main thing a parent wants to see.
16. **Medium: the consent and apply forms are very long single pages** with no sections or progress cue, filled in on phones.

### Accessibility (WCAG 2.2 AA)

Already good, and to keep: skip link, labelled controls, `aria-current` in navigation, error summary with per-field errors, `role="status"` results, unique accessible names on repeated buttons, captions on library tables.

17. **High: contrast is unverified** (open checklist item 1.4.3/1.4.11). Default blue links pass on white, but there is no designed palette to verify.
18. **Medium: target size (2.5.8).** Native controls and inline text links are small; the browser test baseline already records `target-size` on ForgotPassword.
19. **Medium: no visible focus style beyond browser defaults**, and `h1:focus { outline: none; }` in `app.css` removes it on headings.
20. **Low: `/not-found` has no `<title>`** (browser test baseline).
21. **Low: the reconnect modal is the template's**, unbranded.

### Engineering

22. **Medium: no design tokens or shared primitives.** The redesign needs a token layer (`app.css` custom properties) and a small Razor component set (page header, card/panel, status pill, data list/table wrapper, form field, button, notice, rank emblem, progress) to avoid restyling 40 pages one by one.
23. **Low: dead Bootstrap rules** in `app.css` (`.form-floating`, `.darker-border-checkbox`).

## Opportunities to simplify

- A **role-aware app shell**: product mark, a primary navigation grouped by area (Learn · Teach · Safeguard · Administer), an account menu, and a mobile navigation drawer or bottom bar.
- **Home as a real dashboard per role**: student → their rank journey and next lesson; parent → each child's progress and anything needing action (consent); teacher → record today's lesson and their students; Safeguarding Officer and Admin → queues with counts.
- **Lesson page** per lesson, with files as "resources" now and room for in-app content later.
- **Shared review-queue pattern** for registration review, registration approval, consent review and proposal approval: list → detail → decision. Four pages doing the same job four ways today.
- **Responsive data pattern**: table at desktop, stacked rows at phone width, for every admin table.

## Next steps

1. Resolve the blocker, then inspect every role's pages at 375, 768 and 1280 px and complete this audit.
2. Phase 3 design direction, informed by the external research (Orthodox Illustration Project terms, Khan Academy, BBC GEL, typefaces).
