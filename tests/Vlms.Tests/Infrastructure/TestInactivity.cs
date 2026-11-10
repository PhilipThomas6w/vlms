using Vlms.Domain;
using Vlms.Infrastructure;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Seeds staff inactivity the way the app records it (functional.md FR-017, amended 03/11/2026): an
/// open <see cref="StaffInactivity"/> period for one person in one chapter. <see cref="AppUser.HasLeft"/>
/// is retired and no longer means anything, so tests mark someone Inactive with this, not that flag.
/// Adds the row only; the caller saves.
/// </summary>
internal static class TestInactivity
{
    public static StaffInactivity MarkInactive(
        VlmsDbContext db, int userId, int chapterId = Chapter.ShrewsburyId, DateTime? at = null, int? byUserId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var period = new StaffInactivity
        {
            UserId = userId,
            ChapterId = chapterId,
            InactiveAt = at ?? DateTime.UtcNow,
            MarkedInactiveByUserId = byUserId
        };
        db.StaffInactivities.Add(period);
        return period;
    }
}
