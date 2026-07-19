using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// Satisfied if the caller holds ANY one of a fixed set of roles — e.g. "Admin or Teacher" for the
/// guardian-link creation page (functional.md FR-004). <see cref="RoleRequirement"/>/
/// <see cref="RoleAuthorizationHandler"/> can't express this: Program.cs wires exactly one
/// "RequireX" policy per <c>Role</c> enum value, each requiring that single role. Additive, not a
/// replacement — existing single-role "RequireX" policies are untouched.
/// </summary>
public sealed class AnyRoleRequirement : IAuthorizationRequirement
{
    public AnyRoleRequirement(params Role[] roles)
    {
        if (roles is null || roles.Length == 0)
        {
            throw new ArgumentException("At least one role must be specified.", nameof(roles));
        }

        Roles = roles;
    }

    public IReadOnlyCollection<Role> Roles { get; }
}
