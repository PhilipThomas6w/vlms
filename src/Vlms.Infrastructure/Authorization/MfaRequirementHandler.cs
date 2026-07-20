using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// Satisfies an <see cref="MfaRequirement"/> unless the caller holds <see cref="Role.Admin"/> or
/// <see cref="Role.SafeguardingOfficer"/> without <see cref="ICurrentUserContext.HasMfaEnabled"/> —
/// see <see cref="MfaRequirement"/>'s doc comment for the full scope decision this encodes.
/// </summary>
public sealed class MfaRequirementHandler : AuthorizationHandler<MfaRequirement>
{
    private readonly ICurrentUserContext _currentUser;

    public MfaRequirementHandler(ICurrentUserContext currentUser)
    {
        _currentUser = currentUser;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        var holdsMfaGatedRole = _currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer);

        if (!holdsMfaGatedRole || _currentUser.HasMfaEnabled)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
