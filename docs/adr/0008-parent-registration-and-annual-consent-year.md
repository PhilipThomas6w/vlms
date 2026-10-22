# 0008 - Parent-led registration with Admin approval; consent year ends 31 August; separate consent fields

## Status

Accepted in principle (Philip, 22/10/2026), from the gap analysis of the earlier build's US-001 "First-Time Student Registration" story (STATE.md). Open questions are listed at the end and must be answered before the registration item is built. Amends `requirements/functional.md` FR-001, FR-003, FR-004 and FR-015, and the `StudentGuardianLink` hard constraint in `VISION.md` and `CLAUDE.md`.

## Context

The built system has staff register a child (Admin or Teacher creates the `Student` and its `StudentGuardianLink`), after which the parent completes the consent form. The earlier build's story had the parent register the child and an approval create the records. The gap analysis (22/10/2026) also found: consent lasts a rolling 12 months; transport and off-site trips share one consent flag; dietary needs and SEN share one free-text field; there is no alert to Safeguarding Officers or Admins when a form is submitted and no email to the parent; and nothing prevents a duplicate student.

## Decisions (Philip, 22/10/2026)

1. **Parents sign up and register their children.** A parent creates an account and registers one or more children, completing the consent form for each child as part of the same registration. An Admin then approves the registration.
   - The parent's submission is an *application*. No `Student` or `StudentGuardianLink` exists until an Admin approves it. Approval is the verification step that the `StudentGuardianLink` constraint exists to guarantee, so the constraint is restated rather than dropped: a `StudentGuardianLink` is created only by an Admin or Teacher action (registering directly, or an Admin approving a parent's application), never automatically from parent input.
   - On approval the Admin places the child in a category. Ranks follow age groups, so an older child is not enrolled at the first rank; the starting rank is chosen at approval, not fixed at the lowest rank.
2. **Consent runs to a fixed 31 August.** It no longer lasts 12 months from submission. (The Orthodox church year starts on 1 September.)
3. **Alerts.** Admins and Safeguarding Officers are alerted when a form is submitted. Parents are also sent periodic reminders.
4. **No duplicate records, but the same name is legitimate.** Children in the same extended family are often named after grandparents, so the same name, and occasionally the same name and date of birth, does not mean the same child.
5. **Separate fields.** Transport (which can be a five-minute trip between our sites) and off-site trips are separate consents. Dietary requirements and special educational needs are separate fields, each long enough for a full description.

## Consequences and design direction

- **Duplicates.** There is no natural unique identifier for a child, so a database rule cannot both stop duplicates and allow two genuine children with the same name and date of birth. The control is the Admin's approval: the approval page lists existing students with the same name and date of birth (and same-named siblings), and the Admin must choose "same child" (link the guardian to the existing record; no new student) or "different child" before approving. The one case blocked outright is the same parent submitting the same name and date of birth twice while an earlier application or record exists.
- **Consent year.** `PeriodEnd`/`ExpiryDate` become the next 31 August on or after submission (subject to open question 2). Expiry reminders and the expiry digest then fall together each August, which concentrates renewal work at the start of the programme year.
- **Schema.** Splitting the transport/off-site flag and the dietary/SEN field needs an EF migration. Dietary and SEN stay in `ConsentSensitiveDetails` (restricted, read-audited). Existing rows: there is no live data (clean start, `VISION.md`), so test data may be migrated simply (both new flags take the old flag's value; the old combined text goes to dietary requirements).
- **DPIA.** Parents entering children's details before any staff check, and a public sign-up page, are both new processing; the DPIA must cover them.

## Open questions (Philip)

1. **Categories.** What are the categories, and which ranks and ages does each cover? There is no age group in the rank data yet (`seed/curriculum.json` lists 10 ranks only). Does the Admin pick a category and the child starts at its first rank, or pick the starting rank directly?
2. **Late-year forms.** A form submitted in July or August would expire within weeks. Should forms submitted from a cut-off date (for example 1 July) run to 31 August of the following year?
3. **Parent reminders.** What should they remind about, and how often? Proposed: renewal reminders before 31 August (for example 30, 14, 7 and 1 days before) and a weekly reminder while a child has no approved consent in force or a rejected form is waiting for resubmission.
4. **Who approves registrations.** Admins only, or Safeguarding Officers too (they already approve consent)? Can Teachers still register a child directly, for example from a paper form?
5. **Guardian details.** Self-registration needs more than one contact field. Proposed: phone number, relationship to the child, and postal address. Required or optional?
