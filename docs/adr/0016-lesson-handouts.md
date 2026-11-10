# ADR-0016: Lesson handouts

Status: **Accepted** (Philip, 10/11/2026). Amends FR-006 (lesson files) and FR-024 / `0012` ("a rank's original files leave the lesson library once it is converted and reviewed"); extends `0013` (what a suggestion carries).

## Context

Every rank is now converted to app-hosted lesson text (`0012`). Each lesson still lists its original Word documents and photos as downloadable files (FR-006), most of which duplicate the lesson text. Some are not duplicated: print-and-cut templates and other printables (e.g. R2-06's fasting dial template, which its Teacher's Information tells teachers to cut out). Philip (10/11/2026): "we should have the option to attach handouts, and we should leave any handouts in place."

## Decision

1. **Handouts.** A lesson's downloadable files become its **handouts**: printables that go with the lesson (templates, worksheets, colouring pages and the like), not copies of the lesson text.
2. **Attached through Suggest changes.** Adding or removing a handout is part of a lesson suggestion, reviewed and approved like any other change (`0013`), and recorded in the lesson's version history; earlier versions' handouts stay readable from history.
3. **Audience.** Everyone who can see the lesson can see and download its handouts (students, parents and staff), as lesson files today.
4. **Existing files.** An audit classifies every existing lesson file as *handout* (kept) or *duplicate* (its content is already in the lesson text). Philip confirms the list before anything is removed. Duplicates are then removed from the app and from production storage; originals remain in the old SharePoint site and in the git-ignored `seed/` rank folders.
5. **Seed.** Kept handouts are committed under `seed/lessons/<code>/handouts/`, so the curriculum import no longer needs the original rank folders.
