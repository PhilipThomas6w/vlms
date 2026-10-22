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

5. **Reset.** The linked parent (not left) or an Admin with MFA sets a new password through Identity's reset-token path (`GeneratePasswordResetTokenAsync` + `ResetPasswordAsync`), which validates the password before changing anything and rotates the security stamp, ending the student's other sessions at the next revalidation. It refuses an account that has an email, so a parent can't reach an adult's password even if a `Student` row were mis-linked. A reset does not clear an active lockout (same rule as the adult reset).

6. **Forgot password does not apply.** `/Account/ForgotPassword` and `/Account/ResetPassword` look the account up by email; a student has none, so nothing is sent and nothing changes (tested). The pages tell students to ask their parent.

7. **Sign-in.** The login page's field is now "Email or student username" without email-format validation; `PasswordSignInAsync` takes the Identity user name either way. The page keeps its `account-forms` rate limit (per client IP and page) and Identity's per-account lockout, so student sign-ins are covered by both. Students have no MFA requirement (unchanged).

8. **Student page.** `/student` (`RequireStudent`) takes no id. `StudentPageService` resolves the caller's own `Student` by `AppUserId` and reuses `StudentRosterService.LoadRecordAsync`, the teacher view's record query, so progress and reversed-completion handling match. A graduated or inactive student keeps the page.

## Consequences

- Adults: confirmation, uniqueness, reset and lockout behave exactly as before (tested: `StudentAccountServiceTests.AdultConfirmationRule_IsUnchanged`).
- A child's password is known to the parent who set it. That is the decision; it is not a defect.
- Password strength for students uses Identity's defaults, the same as adults (6+ characters, upper, lower, digit, symbol). This may be hard for young children; Philip to decide (see below).
- The Admin page `/administration/student-sign-ins` exists but is not linked from navigation until FR-012.
- `AppUser.Email` is empty for students. Any future feature that emails `AppUser.Email` must skip empty addresses.

## Open questions for Philip

1. **Password rules for children.** Keep the adult rules, or relax them for student sign-ins only (for example 8+ characters, no symbol requirement)? Relaxing needs a custom `IPasswordValidator` that knows the account is a student's.
2. **Lockout for children.** 5 failed attempts lock the account for 15 minutes, and a parent's reset does not unlock it. Should a parent's reset also clear the lockout?
3. **Leavers.** A student marked Inactive or Graduated can still sign in and see their page. Should leaving disable the sign-in?
4. **Admin creation.** Only a linked parent can create a sign-in. Should an Admin also be able to, for families without a parent account?
