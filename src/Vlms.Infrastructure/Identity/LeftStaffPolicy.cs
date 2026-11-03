using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// The rules for staff marked Inactive (functional.md FR-017 as amended 27/10/2026 and 03/11/2026):
/// <list type="number">
/// <item>A chapter Admin marks a Teacher, Admin or Safeguarding Officer Inactive in that chapter
/// (<c>Retention.LeavingService.MarkInactiveAsync</c>), which opens a <see cref="StaffStepDown"/>
/// period. Every role they hold in that chapter, Parent included, stops counting at once, open
/// sessions included: <c>Security.IdentityCurrentUserContext.HasRole</c> and
/// <c>Chapters.ChapterMembership</c> read the open period, and every policy, chapter service and
/// sensitive-data query filter reads those. Other chapters are unaffected.</item>
/// <item>Nobody is locked out of signing in: an Inactive person can still sign in, sees which chapters
/// they are Inactive in, and may apply to register a child. <see cref="IsLockedOut"/> therefore
/// always answers false; it stays as the one hook the sign-in, confirmation and reset paths call.</item>
/// <item>Their staff roles still count for the account-wide MFA rule
/// (<see cref="HoldsCountingStaffRoleInAnyChapter"/>; docs/adr/0010-chapters.md assumption 9, amended
/// 03/11/2026).</item>
/// </list>
/// <b>Role rows are kept, not revoked.</b> They are the history and what reactivation
/// (<c>LeavingService.ReactivateAsync</c>) restores. <see cref="RoleAssignmentService.RevokeAsync"/>
/// refuses to remove a staff role while the person is Inactive in that chapter; a role granted
/// while Inactive counts only from reactivation.
///
/// <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/> are retired (03/11/2026): nothing sets
/// or reads them.
/// </summary>
public static class LeftStaffPolicy
{
    /// <summary>The roles an Admin can mark someone Inactive for.</summary>
    public static readonly IReadOnlyList<Role> StaffRoles = [Role.Teacher, Role.Admin, Role.SafeguardingOfficer];

    /// <summary>True for <see cref="Role.Teacher"/>, <see cref="Role.Admin"/> and <see cref="Role.SafeguardingOfficer"/>.</summary>
    public static bool IsStaffRole(Role role) => role is Role.Teacher or Role.Admin or Role.SafeguardingOfficer;

    /// <summary>
    /// Whether <paramref name="userId"/> holds a Teacher, Admin or Safeguarding Officer role in any
    /// chapter, Inactive there or not. Used for the account-wide MFA rule (docs/adr/0010-chapters.md
    /// assumption 9, amended 27/10/2026 and 03/11/2026).
    /// </summary>
    public static bool HoldsCountingStaffRoleInAnyChapter(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppUsers.Any(u => u.Id == userId
            && u.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer));
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may not sign in at all. Always false since FR-017 was
    /// amended on 27/10/2026: an Inactive person can still sign in.
    /// </summary>
    public static bool IsLockedOut(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return false;
    }

    /// <summary>Async form of <see cref="IsLockedOut"/>.</summary>
    public static Task<bool> IsLockedOutAsync(VlmsDbContext db, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Task.FromResult(false);
    }
}
