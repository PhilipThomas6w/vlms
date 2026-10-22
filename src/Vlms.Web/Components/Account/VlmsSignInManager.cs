using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vlms.Infrastructure.Identity;

namespace Vlms.Web.Components.Account;

/// <summary>
/// Identity's <see cref="SignInManager{TUser}"/> with one change (functional.md FR-011,
/// adr/0007-student-sign-in-accounts.md, Philip 22/10/2026): a <b>student</b> sign-in
/// (<see cref="StudentSignInPolicy.IsStudentAccount"/>) always gets a persistent cookie, so the
/// student stays signed in on their own phone or tablet instead of typing the password each time
/// the app opens. Its lifetime is <see cref="StudentSignInPolicy.PersistentSignInLifetime"/>
/// (90 days), sliding: the cookie handler renews a ticket for its original span once half of it has
/// passed, so a student who uses the app at least every 90 days stays signed in.
///
/// Adults are untouched: their "Remember me" choice and the cookie handler's default lifetime
/// (14 days, sliding) apply exactly as before.
///
/// Every sign-in path goes through <see cref="SignInWithClaimsAsync"/> (password, two-factor,
/// refresh), so the rule can't be bypassed by the page that calls it. A refresh passes the existing
/// ticket's properties, whose expiry is already set, so the window is not reset by a refresh.
/// Revocation is unaffected: the cookie still carries the security stamp, which
/// <see cref="SecurityStampValidator"/> rechecks every
/// <see cref="VlmsIdentityPolicy.SecurityStampValidationInterval"/>, persistent or not.
/// </summary>
public sealed class VlmsSignInManager : SignInManager<VlmsIdentityUser>
{
    private readonly TimeProvider _timeProvider;

    public VlmsSignInManager(
        UserManager<VlmsIdentityUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<VlmsIdentityUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<VlmsIdentityUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<VlmsIdentityUser> confirmation,
        TimeProvider? timeProvider = null)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override Task SignInWithClaimsAsync(
        VlmsIdentityUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (StudentSignInPolicy.IsStudentAccount(user))
        {
            authenticationProperties ??= new AuthenticationProperties();
            authenticationProperties.IsPersistent = true;
            authenticationProperties.ExpiresUtc ??= _timeProvider.GetUtcNow() + StudentSignInPolicy.PersistentSignInLifetime;
        }

        return base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
    }
}
