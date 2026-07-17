using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// A Teacher is authorized for any <see cref="Student"/> — no resource scoping, only the role
/// check (`docs/design/low-level-design.md` "Authorization model": Teachers see all students so
/// they can cover for each other).
/// </summary>
public sealed class TeacherStudentAccessHandler : AuthorizationHandler<StudentAccessRequirement, Student>
{
    private readonly ICurrentUserContext _currentUser;

    public TeacherStudentAccessHandler(ICurrentUserContext currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StudentAccessRequirement requirement, Student resource)
    {
        if (_currentUser.HasRole(Role.Teacher))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
