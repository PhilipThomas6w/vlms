using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// A pure role check — "does the caller hold this <see cref="Role"/>?" — against
/// <see cref="ICurrentUserContext"/> (adr/0002-roles-as-application-claims.md). One instance per
/// <see cref="Role"/> value, wired to an ASP.NET Core <c>AddAuthorization</c> policy per role in
/// Vlms.Web's Program.cs. See <see cref="RoleAuthorizationHandler"/>.
/// </summary>
public sealed class RoleRequirement : IAuthorizationRequirement
{
    public RoleRequirement(Role role)
    {
        Role = role;
    }

    public Role Role { get; }
}
