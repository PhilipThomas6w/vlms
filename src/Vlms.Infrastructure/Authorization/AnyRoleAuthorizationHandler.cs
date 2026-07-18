using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>Satisfies an <see cref="AnyRoleRequirement"/> if the caller holds any one of its roles.</summary>
public sealed class AnyRoleAuthorizationHandler : AuthorizationHandler<AnyRoleRequirement>
{
    private readonly ICurrentUserContext _currentUser;

    public AnyRoleAuthorizationHandler(ICurrentUserContext currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AnyRoleRequirement requirement)
    {
        if (requirement.Roles.Any(role => _currentUser.HasRole(role)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
