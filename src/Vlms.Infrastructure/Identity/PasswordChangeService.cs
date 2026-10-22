using Microsoft.AspNetCore.Identity;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// An adult changing their own password while signed in (FR-011, adr/0007 decision 1). Goes through
/// <see cref="UserManager{TUser}.ChangePasswordAsync"/>, so the current password is checked, the
/// adult rule in <see cref="VlmsPasswordValidator"/> runs, and Identity rotates the security stamp
/// (other sessions sign out at the next recheck). A student account
/// (<see cref="StudentSignInPolicy.IsStudentAccount"/>) is refused here, not only on the page: its
/// password is set and reset by its parent, never by the student.
/// </summary>
public sealed class PasswordChangeService
{
    public const string StudentRefusedCode = "StudentPasswordChange";

    public const string StudentRefusedMessage =
        "A student cannot change their own password. Ask your parent to reset it.";

    private readonly UserManager<VlmsIdentityUser> _userManager;

    public PasswordChangeService(UserManager<VlmsIdentityUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<IdentityResult> ChangeAsync(VlmsIdentityUser user, string currentPassword, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (StudentSignInPolicy.IsStudentAccount(user))
        {
            return IdentityResult.Failed(new IdentityError { Code = StudentRefusedCode, Description = StudentRefusedMessage });
        }

        return await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
    }
}
