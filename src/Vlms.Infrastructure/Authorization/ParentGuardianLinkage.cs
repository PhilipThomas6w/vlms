using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// The one query that expresses "which <see cref="Student"/> rows is this Parent
/// (<see cref="AppUser"/>) linked to", via <see cref="StudentGuardianLink"/> ->
/// <see cref="ParentGuardian.AppUserId"/> (`docs/design/data-design.md` "Guardian link
/// verification"). <see cref="ParentStudentAccessHandler"/> uses this to check a single resource
/// ("is this Student linked to the caller"); <c>Vlms.Infrastructure.Engagement.ParentDashboardService</c>
/// uses it to enumerate all of the caller's own linked students for their dashboard — same
/// relationship, same query, two directions, so it lives in one place instead of being written
/// (and potentially drifting) twice.
/// </summary>
internal static class ParentGuardianLinkage
{
    public static IQueryable<int> LinkedStudentIds(VlmsDbContext db, int parentAppUserId) =>
        db.StudentGuardianLinks
            .Join(db.ParentGuardians, link => link.ParentGuardianId, parent => parent.Id, (link, parent) => new { link.StudentId, parent.AppUserId })
            .Where(x => x.AppUserId == parentAppUserId)
            .Select(x => x.StudentId);

    public static Task<bool> IsLinkedAsync(VlmsDbContext db, int studentId, int parentAppUserId, CancellationToken ct = default) =>
        LinkedStudentIds(db, parentAppUserId).AnyAsync(id => id == studentId, ct);
}
