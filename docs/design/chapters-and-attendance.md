# Chapters and attendance: plan

Status: **decided**, 24/10/2026 (Philip's answers in section 5 override the drafts in sections 2 and 3). Requirements: `requirements/functional.md` FR-020 (attendance) and FR-021 (chapters); merchandise is deferred (`requirements/business-requirements.md`). Each decision below is a draft answer for Philip to confirm or change; nothing here is built yet.

## 1. Why this needs deciding now

The app was built for one parish, with "tens of users" as a hard constraint (`VISION.md`). Chapters change that, and they touch almost every table: who can see which student, who reviews which consent form, who approves whose registration. There is no real data yet ("no data migration, clean start"). Adding chapters now is a contained change; adding them after go-live means splitting live safeguarding data between parishes, which is riskier and harder to prove correct.

**Recommendation:** build chapters before go-live, with Shrewsbury as chapter one. It does not have to delay the redesign; the two can run side by side, with chapters first because the redesign's later pages will show chapter names.

## 2. Chapters: draft decisions

### 2.1 Who is responsible for the data (the most important one)

Each parish is a separate charity with its own safeguarding policy and its own Safeguarding Lead (Shrewsbury's policy names its own). Two workable models:

- **A. Each parish is the controller for its own chapter's members; whoever runs the platform is a processor** for them, under a written data processing agreement with each parish. This matches how safeguarding is already organised (per parish), and keeps each parish answerable for its own children's data.
- **B. A national Varangians body is the controller for everyone.** Simpler on paper, but it needs a legal entity that doesn't exist yet, and it would sit awkwardly with parish safeguarding policies.

**Draft: A.** Today the platform is funded by Shrewsbury Orthodox Church, so Shrewsbury would be both a controller (for its own chapter) and the platform's operator (processor for the others). Whether that suits the trustees, or whether a separate national body should run the platform later, is a question for them. The DPIA and privacy notice change either way.

### 2.2 How chapters are kept apart

- Every chapter-owned record (student, guardian, guardian link, consent and its sensitive details, DBS check, completion, certificate, register, registration application) carries its chapter.
- **Enforced in the same way as today's safeguarding restrictions:** EF Core global query filters, so a query simply cannot return another chapter's rows, plus tests for every entity (ADR-0004's approach, extended). Not left to each page to remember.
- **One database for all chapters**, not one per chapter. It keeps hosting at the current cost (one small VPS) and is enough for dozens of chapters and some hundreds of users. Revisit at around 50 chapters.

### 2.3 Roles per chapter

- **Per chapter:** Teacher, Safeguarding Officer, Admin (a chapter's own administrator), Parent, Student. A person can hold roles in more than one chapter (for example a teacher at two parishes).
- **National:** Curriculum Approver (the curriculum is shared), and a **national administrator** who creates chapters and appoints each chapter's first Admin and Safeguarding Officer.
- **The national administrator does not see any chapter's children, consent or DBS records** unless they also hold a role in that chapter. Least privilege: running the platform is not a safeguarding role.

### 2.4 What stays national

The curriculum (ranks, lessons, files, tags), lesson change proposals and their approval, rank badges, certificate design, and the app's look. Any chapter's teachers can propose a lesson change; the national Approver decides.

### 2.5 Joining and starting a chapter

- **A parent applying** chooses their parish's chapter. That chapter's Safeguarding Officer reviews, and its Admin approves, as now.
- **Starting a chapter:** the national administrator creates it (name, parish, contact). The chapter then needs a signed data processing agreement (model A), its own safeguarding policy, an Admin and a Safeguarding Officer appointed, and its teachers' DBS checks recorded before any child is registered.

### 2.6 Naming

- **"Varangians"** is the national name and brand. A chapter is "Varangians of [parish]", for example "Varangians of the Holy Fathers, Shrewsbury".
- **Signed-in users see their chapter's name;** public pages say "Varangians" without a parish.
- **Fix already needed:** the redesign prototypes currently say "the youth club of Shrewsbury Orthodox Church" in the footer and welcome. That text becomes national.

### 2.7 Reporting

- **Chapter Admins** see their own chapter's reports.
- **The national administrator** sees only anonymous totals per chapter (numbers at each rank, attendance rates), never names.

## 3. Attendance: draft decisions

- **What a register is:** *superseded 24/10/2026 by FR-022: one register per rank-class per event (meeting day), collated into a day total; any Teacher takes or updates any class's register.* Original draft: one register per chapter per meeting date. Sessions are monthly, about two hours, with small groups working at their own rank (syllabus scheme), so a single register per meeting fits.
- **Marks:** present, absent, and absent with apology (the parent let the teacher know).
- **One flow with recording completions:** at a session a teacher takes the register first. Then Record completions offers the students marked present, and the teacher ticks off the lesson each group did. Recording a completion for a child marked absent asks the teacher to confirm, as it may be a catch-up.
- **History:**
  - the student's record shows attendance over the last 12 months;
  - the teacher's student list shows each student's recent attendance;
  - the parent's dashboard shows their own child's attendance.
- **At-risk flag:** today a student is flagged after 8 weeks without a completion. With monthly sessions that is two missed sessions. **Draft:** flag a student after two consecutive absences, or 8 weeks without a completion, whichever comes first.
- **Consent:** a child whose consent has lapsed can still be marked present (they were there), but the register shows the consent warning, and the chapter's Safeguarding Officer is told that a child without consent in force attended. Completions stay blocked, as now.
- **Retention:** attendance is part of the student's record and is deleted with it (3 years after leaving).

## 4. Merchandise (later)

Recorded as deferred. When designed:
- **Payments:** a hosted checkout, so card details never pass through VLMS, which keeps it outside payment-card compliance.
- **Products:** likely a print-on-demand supplier for parish-named items.
- **Data:** buyers' details would be new personal data, so the privacy notice and DPIA change at that point.

## 5. Decisions (Philip, 24/10/2026)

1. **Data responsibility (Philip, 24/10/2026):** Philip, as the app's maintainer, is responsible for securing the data stored in it; he is not responsible for what people enter into it, which rests with each chapter. **What the app holds:** no safeguarding incident reports, ever, and no DBS certificates or disclosures, ever. It only tracks each person's DBS status (check date, expiry, certificate number, status) so renewals are not missed. Wording elsewhere that says "safeguarding records" means consent forms and this DBS status tracking, nothing more.
2. **Build chapters now.** Add a second chapter for testing: **the Greek Orthodox Church of the Holy Transfiguration, Coventry**, alongside Shrewsbury.
3. **Roles.** Philip is the **national administrator**. The national administrator sets up chapters and assigns each chapter's Admins. A chapter's Admins then have admin powers over their own chapter, including appointing other Admins and Safeguarding Officers (and Teachers). Assumption kept from the draft, not contradicted: the national administrator sees no chapter's children, consent or DBS records without also holding a role in that chapter (Philip holds Shrewsbury roles).
4. **The register is self-contained in each chapter.** Only the chapter's own staff take and see its registers. Nationally, attendance is available only as **anonymous** figures (for example per chapter per month: sessions held, students on roll, attendance rate), never names. **Marks: present or absent only** (no "absent with apology"). **At-risk flag: two absences in a row** (Philip, 24/10/2026).
5. **Parents see their own child's attendance and progress** on the parent dashboard.

## 6. Build order

1. **Chapters (FR-021)** on `master`: the `Chapter` entity; chapter on every chapter-owned entity; EF query filters scoped to the signed-in user's chapters, with tests for every entity; per-chapter role assignments; the national administrator role; a chapter switcher for people with roles in more than one chapter; Shrewsbury and Coventry in the development seed.
2. **Attendance (FR-020)**, after chapters: registers per chapter per meeting date, combined with Record completions; the student's attendance on the student record and the parent dashboard; anonymous national figures.
3. The redesign carries on side by side on branch `redesign`, and is merged after chapters so that its later pages can show chapter names.

## 7. Attendance as built (24/10/2026): assumptions for Philip to confirm

Built to sections 3 and 5. Where neither said, the build assumed:

1. **At-risk:** two absences in a row is added alongside the existing 8-week no-completion rule, not instead of it (the draft said "whichever comes first"; section 5 named only the new rule). The report shows which rule flagged each student.
2. **Who:** Teachers take, read and correct the register (`/attendance`); Admins see each student's attendance on the student record but have no register page; Safeguarding Officers and Approvers neither (Safeguarding Officers get the consent alert).
3. **Consent alert:** emailed to the chapter's Safeguarding Officers, sent with the retrying safeguarding-critical priority, naming the children and the date and nothing else; sent once per child per register, not again when the register is corrected.
4. **Corrections:** saving a register again overwrites its marks and records who saved it last; earlier versions are not kept.
5. **Dates:** any past or today's date; never a future one. A student who joins later is simply not on earlier registers.
6. **The student's own page** does not show attendance (only staff and parents were named).
7. **National figures:** the last 12 months, per chapter per month: meetings held, present, absent and the attendance rate, exact counts however small (as for the rank totals).

