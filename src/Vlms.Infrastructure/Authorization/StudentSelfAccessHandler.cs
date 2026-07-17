using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>A Student may act only on the <see cref="Student"/> row matching their own <see cref="Student.AppUserId"/>.</summary>
public sealed class StudentSelfAccessHandler : AuthorizationHandler<StudentAccessRequirement, Student>
{
    private readonly ICurrentUserContext _currentUser;

    public StudentSelfAccessHandler(ICurrentUserContext currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StudentAccessRequirement requirement, Student resource)
    {
        if (_currentUser.HasRole(Role.Student) && _currentUser.UserId is int userId && resource.AppUserId == userId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
