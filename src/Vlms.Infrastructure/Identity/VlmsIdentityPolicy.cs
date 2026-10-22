using Microsoft.AspNetCore.Identity;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// The sign-in policy, set deliberately rather than left at framework defaults (STATE.md "Password
/// reset + Identity hardening"). Applied by Vlms.Web's <c>AddIdentityCore</c> call.
///
/// <b>Confirmed email required.</b> An account cannot sign in until its owner has clicked the
/// confirmation link sent to that address. Without this, anyone could register under someone
/// else's email, and an Admin working through the waiting-for-approval list could grant a role to
/// what looks like a known person's account. Guardian accounts are confirmed on creation: the
/// invite link they used was sent to that address (<see cref="AppUserProvisioningService"/>).
/// Student sign-ins have no email; one counts as confirmed only when a linked parent created it for
/// a student record (<see cref="VlmsUserConfirmation"/>, adr/0007). The rule for every account
/// with an email is unchanged.
///
/// <b>Lockout:</b> 5 consecutive failed password or TOTP attempts lock the account for 15 minutes,
/// for new accounts too. The framework default is 5 attempts / 5 minutes; 15 minutes slows online
/// guessing further while keeping a genuine user's wait short. An adult's own password reset doesn't
/// clear an active lockout. A student password reset by their parent or an Admin does
/// (<see cref="StudentAccountService.ResetPasswordAsync"/>, adr/0007).
///
/// <b>Passwords:</b> adult accounts keep Identity's default rules (6+ characters, upper, lower,
/// digit, symbol). Student sign-ins need only 8+ characters (<see cref="StudentSignInPolicy"/>,
/// enforced by <see cref="VlmsPasswordValidator"/>).
///
/// <b>Session revalidation:</b> the sign-in cookie's security stamp is rechecked every
/// <see cref="SecurityStampValidationInterval"/> (1 minute, FR-016; see below). This applies to every
/// cookie, persistent or not, so a student who stays signed in for weeks
/// (<see cref="StudentSignInPolicy.PersistentSignInLifetime"/>) is still signed out within about a
/// minute of a reset, disable or restore.
///
/// <b>Email-link lifetime:</b> <see cref="EmailTokenLifespan"/> (2 hours) for password-reset and
/// email-confirmation links, against the framework's 1-day default — a reset link sitting in an inbox
/// is a credential. Expired confirmation links can be re-sent from <c>/Account/ResendEmailConfirmation</c>.
/// Configured in Program.cs via <c>DataProtectionTokenProviderOptions</c>, which lives in the
/// web-only Identity assembly. Every reset also rotates the security stamp, so a used link is dead
/// and existing sessions are signed out at their next revalidation.
///
/// <b>Security-stamp validation interval:</b> <see cref="SecurityStampValidationInterval"/>
/// (1 minute), against the framework's 30-minute default
/// (learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.securitystampvalidatoroptions.validationinterval).
/// Used for both the cookie's <c>SecurityStampValidator</c> (static SSR requests, set in Program.cs
/// via <c>SecurityStampValidatorOptions</c>) and the interactive circuit's
/// <c>IdentityRevalidatingAuthenticationStateProvider</c>. A rotated stamp (password reset, 2FA
/// change, role change or leaving — <see cref="SecurityStamps"/>, FR-016) therefore signs existing
/// sessions out within about a minute. The cost is one user lookup per signed-in user per minute.
/// Roles and MFA themselves are re-read on every check regardless (<c>IdentityCurrentUserContext</c>).
/// </summary>
public static class VlmsIdentityPolicy
{
    public const int MaxFailedAccessAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EmailTokenLifespan = TimeSpan.FromHours(2);
    public static readonly TimeSpan SecurityStampValidationInterval = TimeSpan.FromMinutes(1);

    public static void Apply(IdentityOptions options)
    {
        options.SignIn.RequireConfirmedAccount = true;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
    }
}
