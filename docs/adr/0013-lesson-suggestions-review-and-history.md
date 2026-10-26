# ADR-0013: Lesson suggestions, review and history

Status: **Accepted** (Philip, 26/10/2026). Replaces the lesson proposal workflow (FR-007 as first built); amends `0012` decision 8.

## Context

The lesson proposal workflow was designed when a lesson was a set of Word files. A proposal carries the lesson's code, title, order, tags and file list; it cannot carry the lesson text and pictures, which under `0012` are the lesson. It starts from a table of every lesson (about 300), compares a proposal field by field, and on approval overwrites the lesson, so the earlier version is not kept. Philip found it confusing and judged that it could not meet the requirements once every lesson is app-hosted (26/10/2026).

The requirements (Philip, 26/10/2026): a comprehensive lesson review is to be run under an expert panel of clergy and approved catechists, who hold the Approver role. One Approver's approval is enough, and an Approver who is also a Teacher may approve their own change. Every change must keep an audit trail of who asked for it, what the change was, and who approved it. Some lessons will be replaced in their entirety. Later, an Approver will be able to refer a major change to a committee of several Approvers.

## Decision

The workflow follows two patterns panel members are likely to know: track changes in a word processor, and a page's history on Wikipedia.

1. **Suggest changes from the lesson page.** A Teacher opens a lesson and chooses "Suggest changes". An editor opens with the lesson's current text and pictures, a preview, and a required reason for the change. A suggestion can be saved as a draft and submitted when ready. Replacing a whole lesson is a suggestion like any other.
2. **One open suggestion per lesson.** While a suggestion for a lesson is a draft or awaiting review, nobody else can start one for that lesson. Instead they see that a change is in progress. This prevents one approved change silently overwriting another.
3. **Changes to review.** Approvers have one list of submitted suggestions. Each one opens as tracked changes:
   - added text is underlined in green and removed text struck through in red;
   - added and removed pictures are shown;
   - the reason for the change is shown alongside.

   The Approver chooses **Approve** or **Send back** (with a required comment). A suggestion that is sent back returns to its author as a draft. One Approver's approval is enough, and an Approver who is also a Teacher may approve their own suggestion.
4. **Lesson history.** Every lesson keeps a numbered list of versions, and each version is kept permanently.
   - Version 1 is the converted lesson ("Converted from *file*", reviewed by Philip).
   - Each approved suggestion adds the next version, recording who suggested it, its reason, who approved it and when.
   - "Show changes" on any version shows it as tracked changes against the version before.

   This is the audit trail, readable by Approvers and Admins. Suggestions that were sent back are kept with their comments.
5. **Rarer changes use the same editor and review:**
   - the lesson's title, order and tags are a section of the editor;
   - a new lesson is suggested from its rank's lesson list;
   - retiring a lesson is a suggestion with a reason.
6. **The editor works like a word processor**, with bold, headings, lists and "insert picture", and saves Markdown behind the scenes, because most panel members will not know Markdown. It is a JavaScript editor library hosted in the Blazor page. The library is chosen at build, after checking its licence (MIT or similar), its picture handling, and its accessibility; the choice is recorded here. Lesson text stays Markdown rendered with raw HTML disabled (`0012` decision 1).
7. **The old pages and file uploads go.** The "Curriculum: propose a lesson change" page, the Approver's proposal review page and Word file uploads in proposals are removed. Existing proposal rows are not migrated, because the app is not released.
8. **Later, not built yet: committee referral.** An Approver may refer a submitted suggestion to a committee of several Approvers, which then decides it instead. This will be specified when needed, and it fits step 3 as a third choice.
9. **Cut-over from conversion.** While ranks are being converted, the curriculum seeder writes lessons directly and records each as version 1. Once conversion is complete, the seeder never changes an existing lesson's content again, and every change goes through suggestions.

## Consequences

- Panel members work from the lesson itself and see exactly what changed; the history answers who asked, what changed and who approved, for every version, permanently.
- Building it is a new entity set: lesson versions, and suggestions with draft, submitted, approved and sent-back states. It replaces `LessonChangeProposal` and its service and pages. That means one EF migration, and the access-control and WCAG checklists re-reviewed.
- Adds a JavaScript editor dependency, which must pass the browser accessibility checks in the gate (`0009`).
- One open suggestion per lesson means a second panel member waits for the first. This is acceptable at the panel's size and can be revisited if it gets in the way.
- A clickable mock-up of the suggest, review and history screens was approved by Philip on 26/10/2026 (claude.ai/artifact/38SsxBXVsUXWFD7uV7FJoi). He also asked, the same day, for a way to publish a wholesale replacement without the approval process; how is still to be settled.
