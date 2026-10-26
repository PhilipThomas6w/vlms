# ADR-0012: App-hosted lessons

Status: **Accepted** (Philip, 25/10/2026). Amends FR-006 (lesson files) and FR-023 (lessons saved for offline use; `0011`).

## Context

The curriculum is held as files: 314 on the server (about 231 MB), of which 58 are Word documents, 255 images and 1 a PDF. A lesson today is a list of downloads. Survey of `seed/` (25/10/2026):

- 83 `.docx` in all; 47 use Word heading styles, 16 have tables, 15 lists, 11 links, 7 embedded pictures, and only 7 text boxes or drawn shapes, which convert badly. Most convert cleanly.
- Many JPGs are scans of typed pages, not pictures (for example `Herald (5)/1. Prosphora/Images/Prosphora recipe.jpg`, a 2457 × 3484 scan of a recipe and method). They are unreadable to a screen reader (WCAG 2.2 1.1.1, 1.4.5), don't reflow on a phone, and are large: about 70 images are over 1 MB.
- Each rank has a "Teacher's Information" document for staff.

The app has not been released: no teachers or students use it, so nothing has to keep working while lessons change form.

## Decision

1. **A lesson is text and pictures held by the app.** Each lesson gets a body in Markdown (headings, lists, tables, emphasis, links, images) stored with the lesson, and its pictures stored as lesson files. Rendered with Markdig ([github.com/xoofx/markdig](https://github.com/xoofx/markdig)) with raw HTML disabled, so lesson text cannot carry markup or script. Markdown is small, readable when a proposal is compared with the current text, and safe to render.
2. **Converted once, outside the app, and reviewed before import.** Pandoc ([pandoc.org/MANUAL.html](https://pandoc.org/MANUAL.html)) converts each `.docx` to Markdown and extracts its pictures; it is a one-off tool, not a dependency of the app (the alternative is a converter on Microsoft's Open XML SDK, [learn.microsoft.com/office/open-xml/open-xml-sdk](https://learn.microsoft.com/office/open-xml/open-xml-sdk), at more effort). The output is committed under `seed/lessons/` (one Markdown file per lesson code, with its pictures) so every lesson can be read and checked, and the curriculum seeder imports it.
3. **Scanned text becomes lesson text** (Philip, 25/10/2026). The text of scanned pages (prayers, services, recipes, instructions) is written into the lesson body itself, not kept as a picture, with citations where possible and appropriate (for example the service book or translation a prayer comes from). Philip reviews each converted lesson. Where a scanned page is a modern translation still in copyright (for example Chrysostom's *On the Priesthood* in Herald lesson 2), the lesson uses a public-domain translation of the same passage instead, copied in by script from its source and cited (Philip, 26/10/2026: option A), so long copyrighted text is never retyped.
4. **Pictures:** each real picture gets a text alternative written at conversion (Pandoc leaves them empty). The originals stay on the server; a resized copy (about 1,600 px on the long side) is what the lesson page shows and what is saved to a device (Philip, 25/10/2026).
5. **Teacher's Information** becomes a section at the top of each rank's lessons, shown only to Teachers and Admins (Philip, 25/10/2026), never to students or parents.
6. **The lesson page:** `/lessons/{code}`, the body and pictures in order, readable on a phone, with a print layout.
7. **Offline (FR-023):** the lesson page is a plain server-rendered page needing no live connection, so "Save to this device" saves that page and its pictures; there is no separate offline rendering (Philip, 26/10/2026). The same button, with its saved and remove states, is on the lesson page as well as the lesson library, and the same saved-lessons page lists it. A staff member's saved copy includes their Teacher's Information section and its pictures (staff curriculum notes, not personal data); students and parents never receive it, because the server never renders it for them. The existing update check (`GET /lessons/saved`) compares each lesson's content version, so saved copies refresh when a lesson changes. The same content serves the later MAUI app (`0011`).
8. **Editing afterwards** goes through the curriculum workflow (FR-007 to FR-009): a Teacher proposes a change to the lesson text in the app, with a preview, and the Approver approves.
9. **No change-over period** (Philip, 25/10/2026): the app is not yet released, so a rank's Word and scanned files are removed from the lesson library when that rank is converted and Philip has reviewed it. There is no parallel download period.
10. **Pilot first:** Herald's eight lessons, end to end, judged by Philip in the app before the other ranks are converted, one rank at a time.

## Consequences

- The curriculum becomes editable text in the app, accessible and readable on a phone, and much smaller to save offline.
- Converting is real work per lesson: checking Pandoc's output, typing up scanned pages, writing text alternatives and citations, then Philip's review.
- `Lesson` gains a body and a content version; `ProposedLessonContent` carries the body. One EF migration.
- **Permission to reproduce texts: settled** (Philip, 25/10/2026): all the scanned pages have been vetted and may be used. Citations (decision 3) are still added where possible and appropriate.
