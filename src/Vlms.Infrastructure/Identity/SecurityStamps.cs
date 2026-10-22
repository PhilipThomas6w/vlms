using Microsoft.AspNetCore.Identity;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// Rotates a user's ASP.NET Core Identity security stamp after a change to what they may do
/// (functional.md FR-016): a role granted or removed (<see cref="RoleAssignmentService"/>) or a user
/// marked as having left (<c>Retention.LeavingService</c>). A new stamp makes the cookie's
/// <c>SecurityStampValidator</c> (static SSR requests) and
/// <c>IdentityRevalidatingAuthenticationStateProvider</c> (interactive circuits) reject the user's
/// existing sign-ins at their next validation (<see cref="VlmsIdentityPolicy.SecurityStampValidationInterval"/>),
/// so they sign in again. That is a second layer: <c>Security.IdentityCurrentUserContext</c> already
/// re-reads roles and MFA on every check, which is what makes the change take effect immediately.
///
/// Self-service 2FA changes need nothing here: <see cref="UserManager{TUser}.SetTwoFactorEnabledAsync"/>
/// and <see cref="UserManager{TUser}.ResetAuthenticatorKeyAsync"/> rotate the stamp themselves
/// (asserted in <c>RoleChangeTests</c>).
/// </summary>
public static class SecurityStamps
{
    /// <summary>
    /// Calls <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/> for <paramref name="userId"/>.
    /// An <c>AppUser</c> with no paired <see cref="VlmsIdentityUser"/> row has no sign-in to
    /// invalidate, so it is skipped. Throws if Identity reports a failure, so the caller's
    /// transaction rolls the change back rather than leaving old sessions validated.
    /// </summary>
    public static async Task RotateAsync(UserManager<VlmsIdentityUser> userManager, int userId)
    {
        ArgumentNullException.ThrowIfNull(userManager);

        var user = await userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (user is null)
        {
            return;
        }

        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not update the security stamp for user {userId}: "
                + string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
