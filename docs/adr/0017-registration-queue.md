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
