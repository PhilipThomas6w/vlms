# UI/UX redesign: questionnaire answers (draft)

Status: **draft**, 23/10/2026. Claude answered what the repository and `docs/` establish. Items marked **`[Philip]`** need Philip's answer. Answers marked *(Claude's suggestion)* are proposals for Philip to accept or change, not decisions.

Sources: `docs/VISION.md`, `docs/requirements/functional.md`, `docs/requirements/business-requirements.md`, `docs/requirements/user-journeys.md`, `docs/quality/wcag-2.2-aa-checklist.md`, `seed/curriculum.json`, `src/`.

**Correction to the questionnaire's assumptions.** VLMS is not a course/quiz LMS. It has:

- no courses, quizzes, assignments, leaderboards or calendar;
- a fixed **rank ladder** of lessons, which Teachers mark complete during sessions;
- automatic promotion;
- certificates and badges;
- a curriculum-approval workflow;
- in-app safeguarding: consent, medical details and DBS checks.

Half of the application is staff administration and safeguarding. The brief should design for that as much as for the children.

## 1. The application

### 1. Technology stack

- **Framework:** .NET 10, Blazor Web App with Interactive Server rendering (Razor components over SignalR). This is a hard constraint in `docs/VISION.md`. No React, Vue or Node build pipeline.
- **CSS approach:** one hand-written stylesheet, `src/Vlms.Web/wwwroot/app.css` (about 50 lines, left over from the Blazor template), plus Blazor component-scoped CSS (`*.razor.css`). Bootstrap was removed. No Tailwind or preprocessor.
- **Component library:** none. Plain HTML form controls and Blazor's built-in `EditForm` and inputs, plus two shared components of our own (`FormErrorSummary`, and the home-page notices).
- **JavaScript:** none beyond Blazor's own `blazor.web.js` and a service-worker registration. Keep it that way unless there is a clear need.
- **Backend:** ASP.NET Core Identity (TOTP two-factor sign-in), EF Core 10 with PostgreSQL 16 (Npgsql), and QuestPDF for certificate PDFs. Email is sent through Microsoft Graph. A separate .NET worker runs the background jobs.
- **Icons:** none. There is a placeholder app icon (`wwwroot/icons/icon.svg`). Rank badges are SVGs that an Admin uploads per rank (`/administration/rank-badges`).
- **Package manager:** NuGet only. No npm.
- **PWA:** `manifest.json` and a service worker; a Play Store listing as a Trusted Web Activity is planned.
- **Deployment (planned, not yet live):**
  - Self-hosted Linux VPS: DigitalOcean, London.
  - Kestrel behind Caddy, which handles TLS, at `www.varangians.co.uk`.
  - Backups to Cloudflare R2.
- **Constraints the redesign must respect:**
  - The build gate (`build/verify.ps1`) must pass: warnings are errors, all tests pass, the secret scan is clean, and the ASVS and WCAG checklists are re-reviewed whenever a page changes.
  - Many tests assert on page text and element ids.
  - Fonts or scripts loaded from a CDN would be a new external dependency: a privacy question for a children's app, and a Content Security Policy question.

### 2. Maturity and size

- **Pages and components:** 40 routable pages, 53 Razor components in total.
- **Function:** functionally complete for MVP. About 1,000 automated tests.
- **Not yet done:** browser walks found and fixed defects. Branding and colour contrast are explicitly open (`STATE.md` decision 6; WCAG checklist item "Colour contrast (1.4.3/1.4.11)").

### 3. Principal areas (actual structure)

| Area | Routes | Used by |
|---|---|---|
| Sign-in and account | `/Account/*`: login, two-factor sign-in, register, confirm email, reset and change password, authenticator setup | Everyone |
| Home ("what to do next") | `/` lists links per role held, plus counts of work waiting | Everyone |
| Lesson library | `/lessons`: every lesson in every rank, with downloadable `.docx` content and images | Everyone |
| Student's own progress | `/student`: rank, completions, badges, certificates | Students |
| Parent area | `/parent/dashboard`, `/parent/consent` (annual consent form), `/parent/student-sign-ins` (create a child's sign-in) | Parents |
| Registration | `/registration/apply` and `/registration/my-applications` (parent applies for each child); `/registration/review` (Safeguarding Officer); `/registration/approve` (Admin); `/registration/students` (Admin registers a child directly) | Parents, Safeguarding Officer, Admin |
| Teaching | `/students` and `/students/{id}`; `/progress/record` (mark lessons complete); `/progress/completions` (correct one); `/students/consent` (read medical details) | Teachers |
| Curriculum | `/curriculum/teacher` (propose a lesson change or upload); `/curriculum/approver` (approve or reject) | Teachers, Approver |
| Safeguarding | `/safeguarding/consent` (review consent forms); `/safeguarding/dbs` (DBS checks) | Safeguarding Officer, Admin |
| Administration | `/administration/students`, `/administration/user-roles`, `/administration/student-sign-ins`, `/administration/rank-badges`, `/guardianship/links`, `/retention/mark-as-left` | Admin |
| Reporting | `/reporting` (progress and at-risk students); `/reporting/records-due-for-deletion` | Admin |

**Curriculum structure:** 10 ranks.

1. Recruit
2. Guardsman
3. Shield-bearer
4. Sword-bearer
5. Herald
6. Torch-bearer
7. Standard-bearer
8. Cross-bearer
9. Christ-bearer
10. Akolouthos

Ranks 1–7 have 8 lessons each; ranks 8–10 are not yet written. Each lesson has one or more topic tags: Church Life, Worship, Church Interior, Mysteries, Prayers, Saints, Church History, Old Testament, New Testament, Church Year, Christian Life. For example, Recruit lessons include "Varangian Guard", "Iconostasis", "The Lord's Prayer" and "How to Behave in Church".

### 4. User roles

Six roles, combinable on one account (for example Teacher + Admin, or Teacher + Parent):

- **Student:** a child, signing in with a username the parent creates.
- **Parent:** a parent or guardian, approved by an Admin.
- **Teacher:** a catechist.
- **Approver:** curriculum sign-off only, with no safeguarding access.
- **Safeguarding Officer.**
- **Admin.**

Clergy have no separate role. **`[Philip]`** Confirm none is needed.

### 5. Ages

**`[Philip]`** What is the age range? The lowest rank is "Recruit", and on approval the Admin picks a starting rank according to an age-group category, so ages span several years. Give the youngest and oldest age, and which age range starts at which rank.

### 6. One interface or cohorts?

- **What exists:** one interface. Children are grouped only by rank. Students see all lessons in every rank, not only their own. There are no classes or cohorts in the data model; Admins assign teachers to students.
- **`[Philip]`:**
  - Should the student experience differ by rank or age? For example, a simpler presentation for the lower ranks.
  - Is a "class" or "company" grouping wanted?

### 7. Devices

- **What we know:** it is built as a PWA with a planned Play Store app, so phones matter for parents and students.
- **Teachers:** they record completions during sessions, probably on a phone or tablet.
- **`[Philip]`:**
  - Confirm the main device for each role.
  - Are tablets used in class?

### 8. Accessibility

- **WCAG 2.2 AA:** required, and enforced as a gate stage. The checklist is `docs/quality/wcag-2.2-aa-checklist.md`, which must be re-reviewed whenever a page changes.
- **Colour contrast:** not yet verified. The redesign must close it: 4.5:1 for text, 3:1 for UI components.
- **Already in place:** form errors are reported in a summary and next to each field; checkboxes have programmatic labels; there is a skip link.
- **`[Philip]`:** Any needs beyond AA? For example dyslexia-friendly type for SEN children (SEN details are recorded on consent forms).

## 2. What is currently wrong

### 9. What specifically is wrong?

**`[Philip]`** Your judgement. Claude's observations from building it:

- **Looks like a template:** it is visually the Blazor template, with almost no styling, no brand, a placeholder icon and default colours.
- **Generic home page:** the home page is a list of links grouped by role. It shows what you can do, not what to do next, apart from the counts of work waiting.
- **Nothing to celebrate progress:** a child's progress and rank are plain tables and text.
- **Navigation is long for people with several roles:** an Admin who is also a Teacher sees a long flat menu.
- **Lessons are downloads:** lesson content is Word downloads, not readable pages. The post-MVP plan is to convert each lesson to an in-app HTML page.
- **Plain tables:** tables are unstyled and will not read well on a phone.

### 10. Who built the current UI?

Claude Code built it, through the loop harness, with Philip directing. No human designer was involved.

### 11. What must be kept?

- **Must stay:**
  - all behaviour and access rules;
  - every page's route;
  - the accessibility features listed in answer 8;
  - the restriction that sensitive details appear only on the pages that audit-log them.
- **Can change:** the presentation layer is otherwise disposable.
- **Test impact:** changing visible wording or element ids will break request-level tests, which must then be updated deliberately, not deleted.
- **`[Philip]`:** Is anything visual worth keeping?

### 12. Information architecture

*(Claude's suggestion)* Yes, within limits:

- **Navigation may change:** grouping, role-based home pages and the order of things.
- **Routes stay** (they are tested and linked from emails).
- **Access rules may not change:** a page's policy is part of the security review.

**`[Philip]`** Confirm.

### 13. Changing workflows

*(Claude's suggestion)* Yes, for presentation and flow (a wizard for registration, inline actions, fewer page hops), provided the same server-side checks and audit trail apply.

The following are requirements, not UX choices, and must not be shortcut:

- two-stage registration approval;
- the curriculum propose/approve split;
- Admin-only guardian links;
- confirmation before marking staff as left.

**`[Philip]`** Confirm.

## 3. Visual identity

### 14. How Orthodox should it feel?

**`[Philip]`** From your original prompt: the group exists to catechise the children, so the Faith is the purpose and the Varangian story is the frame.

### 15. How Varangian or Anglo-Saxon should it feel?

**`[Philip]`**

### 16. Which heritage should dominate?

**`[Philip]`** From your original prompt: the name comes from the Anglo-Saxon nobility who, after the Norman conquest, went to Constantinople and came to dominate the Varangian Guard. That story is Anglo-Saxon in origin and Byzantine in setting, not Norse.

### 17. A fusion of Anglo-Saxon and Byzantine?

**`[Philip]`** *(Claude's note)* This fits the content: the first Recruit lesson is "Varangian Guard", and the ranks run from guard ranks (Guardsman, Shield-bearer) to liturgical ones (Cross-bearer, Akolouthos, a Byzantine court and liturgical title). The ladder itself tells the story from guardsman to servant of the Church, which could be the design's spine.

### 18. Orthodox motifs to include or avoid

**`[Philip]`**

### 19. Varangian and Anglo-Saxon motifs to include or avoid

**`[Philip]`** *(Claude's note)* Several ranks are named after objects: shield, sword, torch, standard, cross. Rank badges could use those objects without weapons dominating the interface.

### 20. Existing logo, crest or branding

**`[Philip]`**

- **In the repository:** only the placeholder `icon.svg`, and per-rank badge SVGs if an Admin has uploaded any.
- **Lesson material:** the lesson folders (`seed/`, not committed) contain about 270 JPGs, 17 PNGs and a few WebPs, some of them icons. These are content, not branding.

### 21. Open to changing dark blue and gold?

From your original prompt: dark blue and gold are the traditional colours, but you are "not wedded to them". **`[Philip]`** Should they stay recognisable (for example, kept as the core with supporting colours added), or is a full rethink allowed? Any palette must meet the contrast ratios in answer 8.

### 22. Websites or apps whose style you like

**`[Philip]`**

## 4. Icons and sacred imagery

### 23. Can icons of Christ, the Theotokos and saints appear?

**`[Philip]`** (Lesson content already includes icon images, for example the "Iconostasis" and "Common Icons" lessons.)

### 24. Reserve sacred icons for content and devotional contexts?

**`[Philip]`** *(Claude's suggestion)* Yes. Never use an icon as a badge, reward, button, loading spinner or decoration.

### 25. Lines never to cross

**`[Philip]`**

### 26. Avoid generated or AI-imitation iconography?

**`[Philip]`** *(Claude's suggestion)* Yes. Use only real icons, with their source and rights recorded.

**`[Philip]`** Also, who can confirm the rights to the images in the lesson material?

## 5. Character of the experience

### 27. Five adjectives

**`[Philip]`**

### 28. Joining the Varangians rather than logging into an LMS?

**`[Philip]`** From your original prompt: guardians of the Emperor, who was Defender of the Faith, so the children are raised to represent their own faith well. That points to membership and responsibility rather than play. *(Claude's note)* The data model already supports this: children hold a rank, are promoted, and receive a badge per rank and a certificate per lesson.

### 29. How far should the metaphor go?

**`[Philip]`**

- **Already in use:** "rank" (the ten names above), "Recruit", and "badge".
- **Things the questionnaire suggests that don't exist:** cohorts, courses and achievements beyond badges and certificates.

### 30. Conventional terminology for parents and younger children?

*(Claude's suggestion)* Yes, for:

- everything parents and staff use: consent, registration, safeguarding, sign-in;
- every legal or safety meaning.

Themed language belongs only in the child's progress experience.

**`[Philip]`** Confirm.

## 6. Gamification

### 31. Is there gamification now?

Yes, structurally, though barely presented:

- **Rank ladder:** 10 ranks.
- **Automatic promotion:** a child moves up when every lesson in the current rank is complete.
- **Badges:** one per rank, from an Admin-uploaded SVG.
- **Certificates:** a PDF per lesson completion.
- **Parent emails:** parents are emailed on completion and on promotion.

Completions are recorded only by a Teacher, never by the child.

### 32. Would you like more?

**`[Philip]`**

### 33. What should it reward?

- **Today:** completing lessons, as recorded by the Teacher.
- **`[Philip]`:** Anything else, for example attendance or virtues? This would be new functionality, not a redesign.

### 34. Leaderboards, or personal and communal progression?

- **Today:** no leaderboards.
- **`[Philip]`:** Decide.
- *(Claude's note)* The pastoral concern the questionnaire raises is real. Personal progression fits the existing rank model.

## 7. UX for catechesis

### 35. A typical session

**What exists today, by role:**

- **Teacher, in a session:**
  1. Sign in with two-factor.
  2. Record completions: pick a lesson, tick the students who completed it, save.
  3. Promotions happen automatically.
  4. Parents are emailed.
- **Student, at home:**
  1. Sign in.
  2. See their rank, badges and certificates on `/student`.
  3. Open the lesson library and download lesson documents.
- **Parent:**
  1. See each child's progress on the dashboard.
  2. Complete or renew the annual consent form.
  3. Create a sign-in for each child.

**`[Philip]`** Describe a real Sunday session: who uses a screen and when.

### 36. Home use, teacher-led sessions, or both?

Both, by design:

- **Lessons:** delivered by Teachers in sessions, and completions are recorded there.
- **Lesson material:** open to students and parents at home. This is the app's "core purpose" (`business-requirements.md`).

**`[Philip]`** Confirm the balance.

### 37. Lesson content forms

- **Today:** Word documents plus images and handouts, opened or downloaded in the app.
- **Planned (post-MVP):** one printable HTML page per lesson, read in the app.
- **Not in scope:** video, audio, quizzes and interactive activities.
- **`[Philip]`:** Should the redesign anticipate the HTML lesson page?

### 38. Linear lessons or free exploration?

- **Linear for progression:** lessons have a delivery order within each rank, and promotion needs every lesson in the rank.
- **Free for reading:** every lesson in every rank is open to read.

### 39. Do catechists need a different UX?

Yes, already. Their work is task-focused: recording completions, viewing students and medical details, and proposing lesson changes. Staff pages should be efficient rather than themed.

### 40. What catechists spend most time on

- **`[Philip]`:** Confirm.
- **Likely order:**
  1. recording completions;
  2. reading the lesson material;
  3. looking up a student's progress or medical details;
  4. occasionally proposing a lesson change.

## 8. Claude Code's role

### 41. Scope of the first prompt

**`[Philip]`** *(Claude's suggestion)* The full list, plus a requirement that every recommendation states how it works with Blazor Server and a NuGet-only project. Many design plugins assume React, Tailwind or npm.

### 42. What "plugins" means

**`[Philip]`**

**Already installed in this environment:**

- Claude Code plugins: `frontend-design`, `figma`, VectorLab UX skills, the `design` plugin (accessibility review, design critique, design system, UX copy), and `loop-harness`.
- Browser automation: Claude in Chrome, the browser extension. It must be connected for visual checks.

### 43. Specialist capabilities to investigate

*(Claude's suggestion)*

- **Investigate:** accessibility auditing, browser automation and screenshots at several viewport sizes, visual regression, and design-system generation.
- **Not needed:** front-end framework tooling.

**`[Philip]`** Confirm.

### 44. Can the second prompt modify the repository directly?

**`[Philip]`**

**Applies either way:**

- Work goes through `STATE.md` items.
- Done means `pwsh -File build/verify.ps1` exits 0.
- ASVS and WCAG checklist rows are added for each changed page.

### 45. Audit the whole application first?

*(Claude's suggestion)* Yes: Discover → Audit → Design system → Prototype → Review → Implement → Test → Polish.

**`[Philip]`** Confirm.

### 46. Whole application, or one representative page first?

**`[Philip]`** *(Claude's suggestion)* First the design system, then one child-facing page (`/student`) and one staff page (`/progress/record`), and Philip approves before the rest.

### 47. Use browser tooling at desktop, tablet and mobile sizes?

*(Claude's suggestion)* Yes, against the running app with the local PostgreSQL database, signed in as each role. The test accounts are in the project's local setup.

**`[Philip]`** Confirm.

### 48. Preserve functionality and tests?

*(Claude's suggestion)* Yes:

- Behaviour, access rules and the audit trail are unchanged.
- When wording or markup changes, the tests that assert on it are updated in the same commit, never deleted to make the gate pass.

**`[Philip]`** Confirm.

### 49. Git, branches and commits

- **Git:** yes. `master`, pushed to GitHub, is the main branch.
- **Current rules:**
  - Commits without Co-Authored-By trailers.
  - Pushing `master` is approved.
  - Work may run in worktrees per `CLAUDE.md`'s working model.

**`[Philip]`** Should the redesign work on a long-lived branch instead?

### 50. Model

Claude Opus. The project settings use the `opus` alias (currently Opus 5.5), with effort as the cost lever.

**`[Philip]`** Confirm for the redesign.
