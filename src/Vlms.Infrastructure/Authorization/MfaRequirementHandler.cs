using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// Satisfies an <see cref="MfaRequirement"/> unless the caller holds <see cref="Role.Admin"/>,
/// <see cref="Role.SafeguardingOfficer"/>, <see cref="Role.Teacher"/> (each in <b>any</b> chapter,
/// whatever the active chapter: docs/adr/0010-chapters.md assumption 9, amended 27/10/2026; a staff
/// leaver's rows do not count) or <see cref="Role.NationalAdministrator"/> without
/// <see cref="ICurrentUserContext.HasMfaEnabled"/> —
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
        // Staff in any chapter, not only the active one: one account, one password (ADR-0010 assumption 9).
        var holdsMfaGatedRole = _currentUser.HoldsStaffRoleInAnyChapter
            // Appoints every chapter's Admins (docs/adr/0010-chapters.md), so it is MFA-gated too.
            || _currentUser.HasRole(Role.NationalAdministrator);

        if (!holdsMfaGatedRole || _currentUser.HasMfaEnabled)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
