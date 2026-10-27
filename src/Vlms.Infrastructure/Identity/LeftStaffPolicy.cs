using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// The one rule for staff who leave (functional.md FR-017, Philip 22/10/2026, clarified 23/10/2026):
/// <list type="number">
/// <item>A Teacher, Admin or Safeguarding Officer who has been marked as having left
/// (<see cref="AppUser.HasLeft"/>) loses everything their staff roles gave them, at once and
/// including open sessions: for an account that has left, the staff roles count as not held
/// (<see cref="StaffRoleCounts"/>, applied by <c>Security.IdentityCurrentUserContext.HasRole</c>,
/// which every policy, staff service and sensitive-data query filter reads).</item>
/// <item>If they hold no other role, they cannot sign in at all (<see cref="IsLockedOut"/>, applied
/// by <see cref="VlmsUserConfirmation"/>, the Web project's <c>VlmsSignInManager</c> on every sign-in
/// step and security-stamp validation, and <see cref="AccountEmailService"/>, which sends them no
/// reset link).</item>
/// <item>If they are also a Parent, they keep Parent access to their own children: their sign-in
/// still works, but only as a parent. Parent-side checks therefore treat a staff leaver as a parent
/// who has <i>not</i> left: their <see cref="AppUser.HasLeft"/> records leaving the staff, not the
/// programme. Each parent-side EF query spells this out (expressions over navigations don't compose
/// simply) as "<c>!u.HasLeft || u.Roles.Any(staff role)</c>", citing this class: the consent-details
/// query filter, <c>ParentConsentService</c>, <c>StudentAccountService</c>,
/// <c>CertificateAccessService</c>, <c>GuardianNotificationService</c> and
/// <c>ConsentNotificationService</c>.</item>
/// </list>
/// An Approver, Parent or Student who leaves keeps access (an Admin can remove an Approver's role).
///
/// <b>Role rows are kept, not revoked.</b> They are the history (who taught, who held Admin, whose
/// DBS checks these are) and what tells a staff leaver apart from a parent who left. To keep that
/// stable, <see cref="RoleAssignmentService.RevokeAsync"/> refuses to remove a staff role from a
/// leaver; it does nothing for them in any case, because those roles already count as not held.
/// </summary>
public static class LeftStaffPolicy
{
    /// <summary>The roles that stop counting when their holder leaves.</summary>
    public static readonly IReadOnlyList<Role> StaffRoles = [Role.Teacher, Role.Admin, Role.SafeguardingOfficer];

    /// <summary>True for <see cref="Role.Teacher"/>, <see cref="Role.Admin"/> and <see cref="Role.SafeguardingOfficer"/>.</summary>
    public static bool IsStaffRole(Role role) => role is Role.Teacher or Role.Admin or Role.SafeguardingOfficer;

    /// <summary>
    /// Whether a role row the user holds counts: always for a non-staff role, and for a staff role
    /// only while the user has not left.
    /// </summary>
    public static bool StaffRoleCounts(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return !db.AppUsers.Any(u => u.Id == userId && u.HasLeft);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> holds a Teacher, Admin or Safeguarding Officer role that
    /// still counts, in any chapter: they hold one and have not left. Used for the account-wide MFA
    /// rule (docs/adr/0010-chapters.md assumption 9, amended 27/10/2026).
    /// </summary>
    public static bool HoldsCountingStaffRoleInAnyChapter(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppUsers.Any(u => u.Id == userId && !u.HasLeft
            && u.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer));
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may not sign in at all: they have left and hold no role
    /// that still counts (no Parent, Approver or Student role). A parent, Approver or student who
    /// left keeps theirs, so only staff leavers with nothing else are caught.
    /// </summary>
    public static bool IsLockedOut(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppUsers.Any(u => u.Id == userId && u.HasLeft
            && !u.Roles.Any(r => r.Role != Role.Teacher && r.Role != Role.Admin && r.Role != Role.SafeguardingOfficer));
    }

    /// <summary>Async form of <see cref="IsLockedOut"/>.</summary>
    public static Task<bool> IsLockedOutAsync(VlmsDbContext db, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppUsers.AnyAsync(u => u.Id == userId && u.HasLeft
            && !u.Roles.Any(r => r.Role != Role.Teacher && r.Role != Role.Admin && r.Role != Role.SafeguardingOfficer), ct);
    }
}
