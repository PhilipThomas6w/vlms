using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// A Parent may act on a <see cref="Student"/> only if reachable via their
/// <see cref="ParentGuardian.AppUserId"/> -&gt; <see cref="StudentGuardianLink"/> -&gt;
/// <see cref="Student"/> (`docs/design/data-design.md` "Guardian link verification";
/// adr/0002-roles-as-application-claims.md). <see cref="ParentGuardian.AppUserId"/> is the
/// self/parent-login link added during this implementation — see STATE.md and data-design.md.
/// </summary>
public sealed class ParentStudentAccessHandler : AuthorizationHandler<StudentAccessRequirement, Student>
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ParentStudentAccessHandler(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StudentAccessRequirement requirement, Student resource)
    {
        if (!_currentUser.HasRole(Role.Parent) || _currentUser.UserId is not int userId)
        {
            return;
        }

        var isLinked = await _db.StudentGuardianLinks
            .Where(link => link.StudentId == resource.Id)
            .Join(_db.ParentGuardians, link => link.ParentGuardianId, parent => parent.Id, (link, parent) => parent.AppUserId)
            .AnyAsync(appUserId => appUserId == userId);

        if (isLinked)
        {
            context.Succeed(requirement);
        }
    }
}
