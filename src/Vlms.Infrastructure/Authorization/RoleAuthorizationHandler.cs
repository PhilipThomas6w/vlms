using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>Satisfies a <see cref="RoleRequirement"/> if the current caller holds that role.</summary>
public sealed class RoleAuthorizationHandler : AuthorizationHandler<RoleRequirement>
{
    private readonly ICurrentUserContext _currentUser;

    public RoleAuthorizationHandler(ICurrentUserContext currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (_currentUser.HasRole(requirement.Role))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
