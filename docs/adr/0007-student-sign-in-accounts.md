# 0007 - Student sign-ins: parent-created username and password, no email

## Status

Accepted (Philip, 22/10/2026, for the requirement; mechanism below implemented 22/10/2026 by an agent, awaiting Philip's review). Implements `requirements/functional.md` FR-011 and journey J4 steps 1–4. Amends nothing; extends `adr/0002` (roles stay app-owned `UserRole` rows) and the sign-in policy from STATE.md's "Password reset + Identity hardening" item.

## Context

Philip decided (22/10/2026) that a parent creates the sign-in for each of their linked children: a username and a password, with no email address, because many students are young children. The parent can reset the password; an Admin can also reset it. Only a parent linked to the child through `StudentGuardianLink`, who has not left, may create or reset it. Students sign in on the normal login page.

The existing sign-in rules were written for adults, who all have an email:

- `VlmsIdentityPolicy` sets `SignInOptions.RequireConfirmedAccount = true`. Identity's default `IUserConfirmation<TUser>` (`DefaultUserConfirmation`) treats an account as confirmed only when its email is confirmed. A student with no email could never sign in.
- Every adult account's Identity `UserName` is its email address (`AppUserProvisioningService.CreateAsync`). `UserName` is unique (Identity's unique index on `normalized_user_name`). `IdentityOptions.User.RequireUniqueEmail` is **not** set (the framework default is false), so email uniqueness for adults already comes from `UserName = Email`, not from a separate email rule.
- `AppUser.Email` is a required, non-null column; changing it would need a migration, and the migration slot was held by another item.
- Password reset (`/Account/ForgotPassword`) is by email link.

The requirement was not to weaken any of this for adults.

## Decision

1. **Confirmation rule: replace `DefaultUserConfirmation` with `VlmsUserConfirmation`** (registered with `IdentityBuilder.AddUserConfirmation<T>()`, the documented extension point; `SignInManager` takes `IUserConfirmation<TUser>` in its constructor since ASP.NET Core 3.0):
   - an account **with** an email is confirmed only when its email is confirmed: the default rule, unchanged;
   - an account **without** an email is confirmed only when a `Student` row's `AppUserId` points at it.

   Only `StudentAccountService.CreateSignInAsync` sets `Student.AppUserId`, for a linked, non-leaver parent, in the same transaction that creates the account. The parent's act is the confirmation. `RequireConfirmedAccount` stays `true`. No page lets a user change or remove their email, so an adult cannot drop their email to skip confirmation; an emailless account that no student points at cannot sign in (tested).

   Alternative rejected: create the student account with `EmailConfirmed = true` and no email. It works with the default confirmation, but it records a false fact ("email confirmed") and would admit *any* emailless account that some future code path marks that way, rather than tying admission to the student link.

2. **Usernames.** A student username is 3–32 characters of letters, digits, `.`, `-` and `_`, starting with a letter or digit (`StudentUsername`). It can never contain `@`. `AppUserProvisioningService.CreateAsync` now refuses an adult "email" without `@`, so adult usernames always contain one. The two namespaces cannot collide, and uniqueness (case-insensitive) is Identity's own unique `normalized_user_name` index. `RequireUniqueEmail` is left unset: turning it on would make Identity's `UserValidator` reject every account without an email.

3. **No email, no migration.** The student's `VlmsIdentityUser.Email` is null. The paired `AppUser.Email` (required, non-null) is the empty string. No code sends email to an `AppUser` that is not a guardian or staff member, so no message is ever addressed to it.

4. **Role.** `StudentAccountService` is the only code that grants `Role.Student`. `RoleAssignmentService.GrantAsync` refuses `Student`, and refuses any role to an account that is a student sign-in, so a child's account can't be made a Teacher or Admin. The `/administration/user-roles` page hides those options.

5. **Reset.** The linked parent (not left) or an Admin with MFA sets a new password through Identity's reset-token path (`GeneratePasswordResetTokenAsync` + `ResetPasswordAsync`), which validates the password before changing anything and rotates the security stamp, ending the student's other sessions at the next revalidation. It refuses an account that has an email, so a parent can't reach an adult's password even if a `Student` row were mis-linked. A successful reset also clears any lockout and the failed-attempt count (decision 2 below, implemented as decision 10).

6. **Forgot password does not apply.** `/Account/ForgotPassword` and `/Account/ResetPassword` look the account up by email; a student has none, so nothing is sent and nothing changes (tested). The pages tell students to ask their parent.

7. **Sign-in.** The login page's field is now "Email or student username" without email-format validation; `PasswordSignInAsync` takes the Identity user name either way. The page keeps its `account-forms` rate limit (per client IP and page) and Identity's per-account lockout, so student sign-ins are covered by both. Students have no MFA requirement (unchanged).

8. **Student page.** `/student` (`RequireStudent`) takes no id. `StudentPageService` resolves the caller's own `Student` by `AppUserId` and reuses `StudentRosterService.LoadRecordAsync`, the teacher view's record query, so progress and reversed-completion handling match. A graduated or inactive student keeps the page.

## Consequences

- Adults: confirmation, uniqueness, reset and lockout behave exactly as before (tested: `StudentAccountServiceTests.AdultConfirmationRule_IsUnchanged`).
- A child's password is known to the parent who set it. That is the decision; it is not a defect.
- Password strength for students is 8+ characters with no character-class rule; adults keep Identity's defaults (decision 9).
- A student stays signed in on any device they sign in on, for 90 days between visits. On a shared device someone must log out; the login page says so.
- The Admin page `/administration/student-sign-ins` exists but is not linked from navigation until FR-012.
- `AppUser.Email` is empty for students. Any future feature that emails `AppUser.Email` must skip empty addresses.

## Decisions (Philip, 22/10/2026)

1. **Passwords and staying signed in.** Student passwords are at least 8 characters, with the complexity rules relaxed for children (no upper-case, digit or symbol requirement; adult rules unchanged). A student stays signed in on their own phone or tablet so they don't enter the password each time the app opens. A parent can reset the password of each of their children (assumed meaning of "reset both passwords"; to confirm).
2. **Lockout.** A parent's (or Admin's) reset clears the lockout.
3. **Leavers.** Leaving does not disable a sign-in. Admins can disable and restore a student's sign-in.
4. **Admin creation.** Not needed: every student has a parent account, because parental consent is mandatory (legal and safeguarding obligation). Sign-ins stay parent-created only.
5. **Parents with several children** (almost all parents): one parent account, linked to every child, submits each child's consent form and creates each child's sign-in.

Previous open questions, answered above: password rules, lockout on reset, leavers, Admin creation.

## Implementation of the 22/10/2026 decisions (agent, 22/10/2026, awaiting Philip's review)

9. **Student password rule.** Identity's password options are global, so the rule is per account. `VlmsPasswordValidator` (registered by `.AddVlmsPasswordRules()`, which replaces Identity's default `PasswordValidator<T>`) subclasses Identity's own validator. For an adult it calls the base validator with the unchanged global options (framework defaults: 6+ characters, upper, lower, digit, symbol), so adult registration, reset and change run the same code with the same options as before (tested against the stock validator). For a student account it requires 8+ characters, not all whitespace, nothing else. A student account is one with no email (`StudentSignInPolicy.IsStudentAccount`), the same test `VlmsUserConfirmation` uses.

   Alternative rejected: permissive global options plus a validator re-implementing the adult rules. It would duplicate Identity's code, and a slip would weaken adult passwords silently.

10. **Reset clears lockout.** `StudentAccountService.ResetPasswordAsync` resets the password, then calls `SetLockoutEndDateAsync(null)` and `ResetAccessFailedCountAsync`, in one transaction. A failed reset (weak password) leaves the lockout in place. The adult self-service reset is unchanged and does not clear a lockout.

11. **Stay signed in.** `VlmsSignInManager` (`Vlms.Web`, registered by `.AddSignInManager<VlmsSignInManager>()`) overrides `SignInWithClaimsAsync`, the step every sign-in path goes through. For a student account it sets `IsPersistent = true` and, if not already set, `ExpiresUtc = now + 90 days` (`StudentSignInPolicy.PersistentSignInLifetime`). **Expiry chosen: 90 days, sliding.** The cookie handler's sliding expiration renews a ticket for its original span once more than half has passed, so a student who opens the app at least every 90 days is never asked for the password. A refresh passes the existing ticket's properties, so it doesn't reset the window. Adults are unchanged: Remember me is their choice, and the handler's default lifetime (14 days, sliding) applies. `Program.cs` sets `SlidingExpiration = true` explicitly.

    **Revocation still works.** The cookie carries the security stamp. `SecurityStampValidatorOptions.ValidationInterval` is set explicitly to 1 minute (`VlmsIdentityPolicy.SecurityStampValidationInterval`, FR-016; the framework default is 30 minutes). It applies to every cookie, persistent or not. A reset, disable or restore rotates the stamp, so the student's devices are signed out within about a minute; open Blazor circuits are rechecked on the same interval (`IdentityRevalidatingAuthenticationStateProvider`).

12. **Disable and restore.** A dedicated flag, `VlmsIdentityUser.SignInDisabled` (column `asp_net_users.sign_in_disabled`, migration `AddStudentSignInDisabled`, default false). `StudentAccountService.DisableSignInAsync`/`RestoreSignInAsync`, Admin with MFA only, set or clear it and rotate the security stamp in the same update. `VlmsUserConfirmation` treats a disabled account as unconfirmed, so `PasswordSignInAsync` returns "not allowed"; the login page tells a student their sign-in is turned off. Identity runs that check before the password check (as it does for an unconfirmed adult email), so anyone typing a disabled username sees the message; it reveals only that the username exists and is off. A reset never touches the flag, so a parent's reset can't re-enable it. Leaving (Inactive or Graduated) doesn't set it. Controls are on `/administration/student-sign-ins`; the parent page shows when a child's sign-in is off.

    Alternatives rejected: a lockout (a parent's reset now clears lockouts, decision 10), and a column on `Student` (every `Student` update copies all properties through `SetValues`, so `PromotionService`, `LeavingService` and `StudentAdministrationService` would silently clear an unknown field).

13. **Several children, one parent.** `/parent/student-sign-ins` lists every linked child, each with create or reset. Tested with a parent of three. There is no Admin create (decision 4).
