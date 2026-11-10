# ADR-0015: Per-lesson teacher's notes and editable Teacher's Information

Status: **Accepted** (Philip, 10/11/2026); **amended the same day, see "Amendment" below (supersedes decisions 3 and 4 and the per-lesson/rank split).** Amends `0012` (Teacher's Information seeder-managed) and `0013` (what a suggestion carries).

## Context

Philip's review of the Cross-bearer import (10/11/2026) found two defects across all converted ranks:

1. The rank's Teacher's Information appears on a lesson page only when the rank has a `-TI.md` file (R2 to R7; not R1 or R8), and nothing in the app lets anyone add or change it: it is seeder-managed (`0013`, curriculum seeder).
2. Teacher-facing material (lesson plans, objectives, notes for the teacher, guidance on how to teach) was converted into lesson text, which students and parents read: R8-02 (not yet in production), and in production R2-03, R5-08 and R7-06 (keyword search; a full read of every lesson is part of this change).

## Decision

1. **Every lesson has teacher's notes.** A staff-only "Teacher's notes" part (Markdown with pictures, the same rules as lesson text) belongs to each lesson version, beside the lesson text. It is never sent to students or parents, in any response: the lesson page, the picture endpoints, and saved offline copies.
2. **Edited and reviewed with the lesson.** "Suggest changes" edits the lesson text and the teacher's notes in one suggestion (a second editor). Review shows tracked changes for both; approval creates one new version holding both; history shows both.
3. **Always offered.** Staff see the "Teacher's notes" section on every lesson. When empty it says "No teacher's notes yet." with the "Suggest changes" link.
4. **Rank Teacher's Information becomes editable** through the same suggest, review and approve workflow, with its own numbered version history, and is shown to staff on every lesson of the rank, empty or not (empty: "No Teacher's Information yet." with a link to suggest it).
5. **Seeder.** Teacher's notes come from `seed/lessons/<code>.notes.md`, pictures in the lesson's folder `seed/lessons/<code>/`. As for lesson text (`0013` decision 9), the seeder records a change as the next Converted version only while the latest version came from the seeder, and leaves app-edited content untouched with a warning. The same applies to rank Teacher's Information once it can be edited in the app.
6. **Content.** Teacher-facing passages are moved out of the lesson text into the lesson's teacher's notes, word for word; nothing is deleted. Production keeps the current text until this ships (Philip, 10/11/2026: no interim hotfix).

## Assumptions (flagged)

- **Who sees notes:** the roles that see Teacher's Information today (Teacher, Admin, National Administrator) plus the **Approver**, who must read the notes to review a suggestion. Not Students, Parents, or a Safeguarding Officer with no other staff role.
- A rank's Teacher's Information suggestion is a separate suggestion from any lesson's, with its own one-open-at-a-time rule.

## Consequences

- One EF migration (notes on lesson versions and lessons; versions and suggestions for rank Teacher's Information).
- The access-control stage of the gate gains the new endpoints; tests prove a Student and a Parent receive no notes from the page, the picture endpoints or the offline copy.
- Cross-bearer's production import waits for this change.

## As built (10/11/2026)

Built as decided, migration `AddTeachersNotes`. Choices the decision left open:

- A Teacher's Information suggestion is a `LessonSuggestion` of a new kind (`TeachersInformation`, no lesson, the rank set), not a separate table, so the review queue, committee referral and Approver alerts apply unchanged. Its versions have their own table, `TeachersInformationVersion`.
- Notes pictures share the lesson's picture rows (one file-name namespace per lesson); a picture only the notes use is flagged `TeachersNotesOnly` and is served only at the staff-only address.
- Teacher's Information pictures are now stored under content-addressed keys, as lesson pictures are, so earlier versions stay readable.
- No data backfill: a rank's Teacher's Information version 1 (Converted) is made by the next seeder run, or when someone first suggests a change to it.
- A `.notes.md` file with no lesson text beside it is a seeder warning and is not imported.

## Amendment (Philip, 10/11/2026, after seeing the build)

The build showed two staff sections on every lesson ("Teacher's Information" for the rank, often empty, and "Teacher's notes" for the lesson). Philip: "Each lesson should include Teacher's Information that includes a lesson outline and guidance on how to present the lesson. That's it." So:

1. **One section per lesson, called "Teacher's Information"**: the lesson outline and guidance on presenting it. Staff-only exactly as decision 1 (same roles, assumption kept), part of each lesson version, edited and reviewed through the lesson's "Suggest changes" (decision 2). Empty state "No Teacher's Information yet." with the suggest link.
2. **No rank-wide Teacher's Information** anywhere: no section, suggestion kind, history page, picture endpoint, seeder input or stored column/table. Supersedes decision 4 and `0012`'s rank-level Teacher's Information.
3. **"Teacher's notes" is not a separate thing**: it is this lesson-level Teacher's Information.
4. **Content**: each rank file `R<n>-TI.md` (R2 to R7) has one section per lesson, in lesson order; each section moves word for word into that lesson's Teacher's Information, ahead of any teacher-facing text already moved out of the lesson text (decision 6). Seeder input becomes `seed/lessons/<code>.teachers-information.md`.
5. The migration `AddTeachersNotes` was never released, so it is replaced by one migration for the final shape.
