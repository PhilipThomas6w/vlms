using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// A Parent may act on a <see cref="Student"/> only if reachable via their
/// <see cref="ParentGuardian.AppUserId"/> -&gt; <see cref="StudentGuardianLink"/> -&gt;
/// <see cref="Student"/> (`docs/design/data-design.md` "Guardian link verification";
/// adr/0002-roles-as-application-claims.md). <see cref="ParentGuardian.AppUserId"/> is the
/// self/parent-login link added during this implementation — see STATE.md and data-design.md.
/// The actual query lives in <see cref="ParentGuardianLinkage"/> — shared with
/// <c>Engagement.ParentDashboardService</c>, which enumerates the same relationship the other
/// direction ("all students linked to this caller" rather than "is this one resource linked").
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

        if (await ParentGuardianLinkage.IsLinkedAsync(_db, resource.Id, userId))
        {
            context.Succeed(requirement);
        }
    }
}
