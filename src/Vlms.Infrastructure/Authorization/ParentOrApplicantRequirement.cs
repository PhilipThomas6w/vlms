using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// "My applications" (adr/0017 As built): a Parent in the active chapter, or someone who has
/// submitted a registration application of their own. Only a Parent can apply since adr/0017, but
/// an application submitted before that change may belong to someone with no role, who must still
/// be able to follow and answer it. Not a student sign-in.
/// </summary>
public sealed class ParentOrApplicantRequirement : IAuthorizationRequirement;

/// <summary>
/// Satisfies <see cref="ParentOrApplicantRequirement"/>. The application check runs on a context of
/// its own (as <c>IdentityCurrentUserContext</c> does for roles): the menu evaluates this alongside
/// the page's own queries, which share the request's DbContext. The <c>StudentApplication</c> query
/// filter shows a caller their own applications, so nothing else can match.
/// </summary>
public sealed class ParentOrApplicantAuthorizationHandler : AuthorizationHandler<ParentOrApplicantRequirement>
{
    private readonly ICurrentUserContext _currentUser;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public ParentOrApplicantAuthorizationHandler(ICurrentUserContext currentUser, DbContextOptions<VlmsDbContext> options)
    {
        _currentUser = currentUser;
        _options = options;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ParentOrApplicantRequirement requirement)
    {
        if (_currentUser.HasRole(Role.Parent))
        {
            context.Succeed(requirement);
            return;
        }

        if (_currentUser.UserId is not int userId || _currentUser.HasRole(Role.Student))
        {
            return;
        }

        await using var db = new VlmsDbContext(_options, _currentUser);
        if (await db.StudentApplications.AnyAsync(a => a.ApplicantUserId == userId))
        {
            context.Succeed(requirement);
        }
    }
}
