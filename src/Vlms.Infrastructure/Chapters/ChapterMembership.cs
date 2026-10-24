using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Chapters;

/// <summary>
/// Which chapters a person belongs to, and which of them they are working in
/// (docs/adr/0010-chapters.md, "Active chapter"). A person belongs to a chapter while they hold a
/// chapter role there that still counts: a staff leaver's Teacher, Admin and Safeguarding Officer
/// rows do not (<see cref="LeftStaffPolicy"/>), their Parent rows do. Reads <see cref="UserRole"/>,
/// <see cref="AppUser"/> and <see cref="ActiveChapterChoice"/> only, none of which is filtered.
/// </summary>
public static class ChapterMembership
{
    /// <summary>The chapters <paramref name="userId"/> holds a counting chapter role in, lowest id first.</summary>
    public static List<int> ChapterIds(VlmsDbContext db, int userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var hasLeft = db.AppUsers.Any(u => u.Id == userId && u.HasLeft);
        return db.UserRoles
            .Where(r => r.UserId == userId && r.ChapterId != null
                && (!hasLeft || (r.Role != Role.Teacher && r.Role != Role.Admin && r.Role != Role.SafeguardingOfficer)))
            .Select(r => r.ChapterId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    /// <summary>
    /// The chapter <paramref name="userId"/> is working in: their last choice if they still belong to
    /// it, otherwise the lowest-numbered chapter they belong to, otherwise null.
    /// </summary>
    public static int? ResolveActiveChapter(VlmsDbContext db, int userId)
    {
        var chapters = ChapterIds(db, userId);
        if (chapters.Count == 0)
        {
            return null;
        }

        var choice = db.ActiveChapterChoices
            .Where(c => c.UserId == userId)
            .Select(c => (int?)c.ChapterId)
            .FirstOrDefault();
        return choice is int chosen && chapters.Contains(chosen) ? chosen : chapters[0];
    }

    /// <summary>
    /// The people who hold one of <paramref name="roles"/> in <paramref name="chapterId"/> and have
    /// not left, for staff notifications: an email about one chapter's child goes to that chapter's
    /// staff only (docs/adr/0010-chapters.md).
    /// </summary>
    public static IQueryable<AppUser> StaffIn(VlmsDbContext db, int chapterId, params Role[] roles)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AppUsers.Where(u => !u.HasLeft
            && u.Roles.Any(r => r.ChapterId == chapterId && roles.Contains(r.Role)));
    }

    /// <summary>The ids of the people holding <paramref name="role"/> in <paramref name="chapterId"/>.</summary>
    public static IQueryable<int> HoldersIn(VlmsDbContext db, int chapterId, Role role)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.UserRoles.Where(r => r.ChapterId == chapterId && r.Role == role).Select(r => r.UserId);
    }
}
