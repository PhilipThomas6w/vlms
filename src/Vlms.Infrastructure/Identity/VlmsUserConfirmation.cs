using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// What "confirmed" means for <see cref="SignInOptions.RequireConfirmedAccount"/>
/// (<see cref="VlmsIdentityPolicy"/>), replacing Identity's <see cref="DefaultUserConfirmation{TUser}"/>
/// (adr/0007-student-sign-in-accounts.md):
/// <list type="bullet">
/// <item><b>An account with an email address</b> (every adult account) is confirmed only when that
/// email is confirmed — exactly the default rule, unchanged.</item>
/// <item><b>An account with no email address</b> is confirmed only when a <see cref="Domain.Student"/>
/// record names it as its sign-in (<see cref="Domain.Student.AppUserId"/>). Only
/// <see cref="StudentAccountService.CreateSignInAsync"/> sets that, for a parent linked to the child,
/// in the same transaction that creates the account. The parent's act is the confirmation.</item>
/// <item><b>An account with <see cref="VlmsIdentityUser.SignInDisabled"/> set</b> is never
/// confirmed: this is how an Admin's disable stops a student signing in (adr/0007).</item>
/// <item><b>A Teacher, Admin or Safeguarding Officer marked as having left who holds no other role</b>
/// is never confirmed (<see cref="LeftStaffPolicy.IsLockedOut"/>, FR-017). One who is also a Parent
/// stays confirmed and signs in as a parent only.</item>
/// </list>
/// An emailless account that no student points at (which no code path creates) can never sign in,
/// and an adult cannot drop their email to skip confirmation: no page edits a user's email.
/// </summary>
public sealed class VlmsUserConfirmation : IUserConfirmation<VlmsIdentityUser>
{
    private readonly VlmsDbContext _db;

    public VlmsUserConfirmation(VlmsDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsConfirmedAsync(UserManager<VlmsIdentityUser> manager, VlmsIdentityUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);

        if (user.SignInDisabled)
        {
            return false;
        }

        // FR-017: a staff member who has left and holds no other role (such as Parent) can't sign in.
        if (await LeftStaffPolicy.IsLockedOutAsync(_db, user.Id))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(user.Email))
        {
            return await manager.IsEmailConfirmedAsync(user);
        }

        // At sign-in the caller is anonymous, so the chapter query filter (docs/adr/0010-chapters.md)
        // would hide every student. Ask as the account itself: the filter's own-record arm shows a
        // student their own record and nothing else, so this sees exactly the one row it needs.
        var options = _db.GetService<IDbContextOptions>() as DbContextOptions<VlmsDbContext>
            ?? throw new InvalidOperationException("VlmsDbContext options are not available.");
        await using var asAccount = new VlmsDbContext(options, new Security.AccountLookupUserContext(user.Id));
        return await asAccount.Students.AnyAsync(s => s.AppUserId == user.Id);
    }
}
