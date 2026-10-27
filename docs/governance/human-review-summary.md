# Human security and accessibility review: summary for Philip

Written 27/10/2026 for the go-live review (`STATE.md` "Human security and accessibility review"). It groups every access-control and accessibility change recorded in `governance/asvs-access-control-checklist.md` and `quality/wcag-2.2-aa-checklist.md` by its effect. Read it, do the spot checks in the running app, then record your sign-off at the end. Signing here countersigns the Claude-written rows in both checklists in one go.

## 1. Who can see what

| Data | Who can see it | Enforced by |
|---|---|---|
| DBS checks | Admin and Safeguarding Officer of the chapter only. Teachers, Approvers, parents and students see nothing, not even that a record exists | EF Core global query filter on the whole entity (`adr/0004`) |
| Consent sensitive details (medical, dietary/SEN, emergency contact) | Admin and Safeguarding Officer; Teachers read only; the child's linked parents read and update | Same filter (`adr/0006`) |
| Students, attendance, progress | Staff of the student's chapter; Safeguarding Officers read attendance but cannot take registers; parents see only their linked children; students only themselves | Chapter filter (`adr/0010`) and page policies |
| Curriculum approval | Approver only; the Approver has no safeguarding access at all | Role policy (`adr/0013`) |
| National figures | National Administrator: totals and per-chapter counts, no individual records | Separate national queries |

Every read of DBS or consent sensitive details writes a row to the read-audit log, which the app's database login cannot change or delete.

## 2. Sign-in

- Two-factor sign-in (authenticator app) is required for anyone holding Teacher, Admin or Safeguarding Officer in any chapter, checked on every request.
- Lockout after repeated failures; password reset and email confirmation by single-use, expiring emailed codes; rate limits on the account forms.
- A Parent–child link is created only by an Admin (directly, or on final approval of an application after a Safeguarding Officer's review), never by a Teacher and never automatically.
- Stepping a member of staff down removes their staff access at once (their security stamp is rotated); reinstating restores their roles. Only an Admin of their chapter can do either, never for themselves, and never for the last Admin.

## 3. Sensitive data and deletion

- Retention: students 3 years after leaving, DBS 6 years, audit log 6 years. An Admin confirms each deletion by typing the person's name; every deletion is logged without personal data; the audit log purges itself through a separate, narrowly scoped database role (`adr/0014`).
- Secrets are never in the repository (the gate scans every commit); the email secret exists only on the server.

## 4. Page accessibility (WCAG 2.2 AA)

- Every page has one heading structure, labelled form fields, visible focus, and works by keyboard; confirmations are on the page, never browser pop-ups.
- The gate runs axe-core against every main page in a real browser on each full run.
- Open: colour contrast (1.4.3, 1.4.11) waits for the palette decision; the current palette is the interim.

## 5. Spot checks (about an hour, in the running app)

Use the local test accounts in `STATE.md` "Local environment" (Coventry) and your own.

1. As a Teacher: open a student; consent details are visible, DBS pages are not reachable (the URL gives "not authorised").
2. As a Parent: only your linked child appears; you can update their consent details; no other child's name appears anywhere.
3. As a Safeguarding Officer: DBS records are visible; you can view a register but not take one.
4. As an Admin: step a Teacher down; sign in as them and confirm staff pages are gone; reinstate them and confirm they are back with two-factor sign-in still working.
5. Sign in as a Teacher without two-factor set up: you are sent to set it up before any staff page.
6. Using only the keyboard (Tab, Enter, Space), sign in and record a lesson completion.
7. Open `/privacy` without signing in.

## Sign-off

| Reviewer | Date | Result |
|---|---|---|
| | | |
