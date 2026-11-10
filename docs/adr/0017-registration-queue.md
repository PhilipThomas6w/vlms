# ADR-0017: One registration queue; roles before access

Status: **Accepted** (Philip, 10/11/2026). Amends FR-021 (as amended 09/11/2026), FR-017 (Inactive staff requesting Parent access), FR-012 (home page) and `0008` (who can apply to register a child, and what approval grants).

## Context

A teacher registered on the live site. Nothing appeared under "Approve registrations"; she was listed only under User roles → New registrations. Today the Admin's registration work is split across pages: account registrations on User roles ("New registrations", "Waiting for approval" for accepted guardian invites) and children's applications on "Approve registrations". Any signed-in adult who is not a student can apply to register a child, and sees the "Register your child" box on Home, staff included. Philip (10/11/2026): "A user registers. They go into a queue to have a role(s) assigned to them. Once they are assigned as a parent, they can register a child. Once they are assigned e.g., a teacher, they can see things a teacher can see. Until they have a role assigned, they see 'registration in process' or words to that effect. There can be a separate page or section for approving student registration." And: "Why would staff need to see the Register your child box unless they themselves were a parent?"

## Decision

1. **One queue.** Everyone who registers, whatever they are joining as (parent, teacher, other), joins their chapter's queue of people waiting for a role. People who accepted a guardian invite and are not yet a Parent join the same queue.
2. **An Admin assigns the role or roles**, from the queue. Granting takes the person off the queue; dismissing takes them off without a role and deletes nothing (as FR-021 item 4).
3. **No role, no access.** Until they hold a role in a chapter, a person sees only that their registration is in progress (Home), with no other menu items.
4. **Only a Parent can register a child.** The "Register your child" box on Home, and the menu's Registration group, are shown only to people holding the Parent role. A member of staff who is also a parent is given the Parent role as well.
5. **One Registrations page for Admins** (replacing "Approve registrations"), with a count of what is waiting in the menu and on the Home Admin card:
   - people waiting for a role (name, email, joining as, registered, email confirmed, how they came: self-registration or guardian invite), with the grant and dismiss controls, and for someone joining as a teacher their DBS check status with a link to add one;
   - children's registration applications: those waiting for the Admin's approval (opened and decided as now) and those still with the Safeguarding Officer (status only).
   User roles goes back to managing the roles of people already in the chapter.

## Consequences

- Approving a child's application no longer grants the Parent role (the applicant already holds it); it still creates the student (or links to an existing one), the guardian record, the link and the approved consent (`0008`). Applications submitted before this change by someone without the Parent role keep working: approval grants Parent if the applicant does not hold it.
- FR-017: an Inactive member of staff who wants Parent access asks an Admin to grant the Parent role, instead of submitting an application.
- The registration email to the chapter's Admins (FR-021 item 3) is unchanged.
- The National Administrator's list of role-less accounts on no chapter's queue (FR-021 item 5) is unchanged.

## Assumptions (flagged)

- Someone dismissed from the queue, still with no role, sees "Your registration was not approved. Please contact your chapter." rather than "in progress".
- Several roles can be granted to one person from the queue in one go.

## As built (10/11/2026)

Choices the decision left open, recorded as built:

- **Where the queue lives.** `Registration/RegistrationQueueService` is the union of the two existing lists, reused rather than rebuilt: `NewRegistrationService.ListAsync` (people who registered for the chapter, whatever they joined as, holding no role there) and `RoleAssignmentService.ListAwaitingApprovalAsync` (accepted guardian invites not yet a Parent there, who may already hold another role). Someone on both is one row. The list and the count share the same query builders (`NewRegistrationService.OpenRequestsIn`, `RoleAssignmentService.InviteAcceptorsAwaitingParentIn`), so they cannot disagree.
- **Granting several roles** is one call, `RoleAssignmentService.GrantAsync(userId, roles)`: all or none, in one transaction with one security-stamp rotation, with every existing guard (chapter roles only, Approver national, no student sign-ins, none already held). The queue offers the four roles a chapter Admin grants, less any already held, as checkboxes, and the grant is confirmed naming the person and the roles. The queue refuses anyone not on it.
- **Who leaves the queue when.** A self-registration leaves once they hold any role in the chapter; a guardian-invite acceptor leaves once they are a Parent there (granting them only Teacher, say, leaves them on it). Only a self-registration can be dismissed: an accepted invite has no request row to mark, and no migration was added for one.
- **No role, no access.** "Holding no role" means no role row at all, chapter or national (Student included). Home then shows only "Your registration is in progress. An Admin of <chapter> will give you access." (several chapters are joined with "or"; an account on no chapter's queue reads "An Admin will give you access."), or, when every registration they made was dismissed and they are on no queue, "Your registration was not approved. Please contact your chapter." An invite acceptor's chapter is read from their own guardian record. The menu already showed such a person nothing but Home once the Registration group became Parent-only.
- **My applications** has its own policy, `RequireParentOrApplicant`: a Parent in the active chapter, or someone with an application of their own (the continuity case: an application submitted before this change by someone who still has no role). Register a child is `RequireParent`. The menu's Registration group shows under the first, Register a child inside it under the second. This was chosen over granting such applicants Parent automatically, which would give a role no Admin decided.
- **Applying.** `StudentApplicationService.SubmitAsync` refuses anyone who is not a Parent in the chosen chapter, counted as `HasRole` counts it: an open Inactive period there suspends it (FR-017). The Apply page lists only the open chapters where the caller is a Parent. Reading and answering one's own applications still needs only a signed-in adult account.
- **Approval** still grants Parent when the applicant lacks it (legacy applications), and is otherwise unchanged.
- **The page.** `/registration/approve` stays the address (old links and the Admins' email keep working), titled "Registrations". "Waiting for a role" is an interactive component (`RegistrationQueuePanel`) inside the static page, so the children's approval forms are unchanged; "Children's applications" holds "Waiting for your approval" (the existing flow) and "With the Safeguarding Officer" (waiting for their review or for the parent's answer; child, parent, submitted date and status only, no restricted details). For someone joining as a teacher the row shows their latest DBS check in this chapter (None, Valid until, Expired on, or Pending/Flagged), read through the `DbsCheck` query filter (audited) with a link to DBS checks.
- **Counts.** The menu's Registrations link and the Home Admin card show people waiting plus applications awaiting the Admin's approval, for an Admin with two-factor sign-in only. The count and the caller's own status run on a context of their own, as the menu initialises alongside the page.
- **The registration email** to the chapter's Admins is unchanged except its link, which now opens Registrations instead of User roles.
- **FR-017 wording.** The Inactive notice on Home no longer offers the application; it reads "If you also want Parent access, ask an Admin of your chapter." Flagged: FR-017 still says no role grant restores access while the person is Inactive in that chapter, so a Parent role granted then counts only after reactivation.

### After release (10/11/2026)

- **Defect:** in production the Registrations page failed ("A second operation was started on this context") once the chapter had applications: the queue panel's prerender ran on the page's DbContext while the page's own queries were in flight. Fixed by not prerendering the panel; it loads over the circuit in its own scope. The tests use SQLite, whose async queries run synchronously, so the race can't be reproduced there; the regression test asserts the structure (no queue rows in the first response).
- **No other way in (Philip, 10/11/2026: "why do we even still have this on the User roles page?").** User roles' "Give someone a role" (find any account on the platform by email and grant it a role here) is removed, and `RoleAssignmentService.GrantAsync` refuses anyone who neither holds a role in the chapter nor is on its queue. Gap, flagged: the National Administrator's "Send to chapter" lists only accounts with no role at all, so someone who already holds a role in another chapter has no way onto this chapter's queue yet.
