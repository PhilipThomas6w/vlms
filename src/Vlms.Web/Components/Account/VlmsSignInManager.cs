using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;

namespace Vlms.Web.Components.Account;

/// <summary>
/// Identity's <see cref="SignInManager{TUser}"/> with two changes. First (functional.md FR-011,
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
///
/// Second (functional.md FR-017, Philip 22/10/2026, clarified 23/10/2026): a Teacher, Admin or
/// Safeguarding Officer who has left and holds no other role (<see cref="LeftStaffPolicy.IsLockedOut"/>)
/// can't sign in at all; one who is also a Parent signs in as a parent only, because their staff
/// roles no longer count (<c>IdentityCurrentUserContext</c>). <see cref="VlmsUserConfirmation"/>
/// already makes them unconfirmed, so Identity's own pre-sign-in check refuses the password and
/// authenticator steps; this class adds the rest in one place:
/// <list type="bullet">
/// <item><see cref="PreSignInCheck"/> reports the refusal as a plain failure, not "not allowed", so
/// the login page shows its generic "Invalid sign-in details." and reveals neither that the account
/// exists nor that it has left. The check runs before the password is verified, so a guesser learns
/// nothing either way.</item>
/// <item><see cref="TwoFactorRecoveryCodeSignInAsync"/> runs the same pre-sign-in check first, so a
/// sign-in half-way through (password accepted just before the leaving) can't be finished with a
/// recovery code.</item>
/// <item><see cref="ValidateSecurityStampAsync(VlmsIdentityUser, string)"/> also requires
/// <see cref="SignInManager{TUser}.CanSignInAsync"/>. Leaving rotates the stamp anyway, but this makes
/// the cookie's <see cref="SecurityStampValidator"/> and the circuit's
/// <c>IdentityRevalidatingAuthenticationStateProvider</c> (which calls this method) refuse a locked-out
/// account even with a current stamp, for example one signed in by a path that skips the
/// pre-sign-in check.</item>
/// </list>
/// </summary>
public sealed class VlmsSignInManager : SignInManager<VlmsIdentityUser>
{
    private readonly TimeProvider _timeProvider;
    private readonly VlmsDbContext _db;

    public VlmsSignInManager(
        UserManager<VlmsIdentityUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<VlmsIdentityUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<VlmsIdentityUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<VlmsIdentityUser> confirmation,
        VlmsDbContext db,
        TimeProvider? timeProvider = null)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<SignInResult?> PreSignInCheck(VlmsIdentityUser user)
    {
        var result = await base.PreSignInCheck(user);
        if (result is { IsNotAllowed: true } && await LeftStaffPolicy.IsLockedOutAsync(_db, user.Id))
        {
            return SignInResult.Failed;
        }

        return result;
    }

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        var user = await GetTwoFactorAuthenticationUserAsync();
        if (user is not null && await PreSignInCheck(user) is { } refusal)
        {
            return refusal;
        }

        return await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
    }

    public override async Task<bool> ValidateSecurityStampAsync(VlmsIdentityUser? user, string? securityStamp) =>
        await base.ValidateSecurityStampAsync(user, securityStamp) && await CanSignInAsync(user!);

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
