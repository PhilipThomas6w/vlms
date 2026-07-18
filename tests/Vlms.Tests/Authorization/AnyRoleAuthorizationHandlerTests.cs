using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Authorization;

/// <summary>Verifies <see cref="AnyRoleAuthorizationHandler"/> succeeds an
/// <see cref="AnyRoleRequirement"/> when the caller holds any one of its roles — the mechanism
/// behind the "RequireAdminOrTeacher" policy gating the guardian-link page (functional.md
/// FR-004).</summary>
public sealed class AnyRoleAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext BuildContext(AnyRoleRequirement requirement) =>
        new([requirement], new ClaimsPrincipal(new ClaimsIdentity()), resource: null);

    [Fact]
    public async Task Succeeds_WhenCallerHoldsTheFirstListedRole()
    {
        var handler = new AnyRoleAuthorizationHandler(new FakeCurrentUserContext(userId: 1, Role.Admin));
        var context = BuildContext(new AnyRoleRequirement(Role.Admin, Role.Teacher));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_WhenCallerHoldsTheSecondListedRole()
    {
        var handler = new AnyRoleAuthorizationHandler(new FakeCurrentUserContext(userId: 10, Role.Teacher));
        var context = BuildContext(new AnyRoleRequirement(Role.Admin, Role.Teacher));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerHoldsNoneOfTheListedRoles()
    {
        var handler = new AnyRoleAuthorizationHandler(new FakeCurrentUserContext(userId: 99, Role.Parent));
        var context = BuildContext(new AnyRoleRequirement(Role.Admin, Role.Teacher));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public void Constructor_WithNoRoles_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AnyRoleRequirement());
    }
}
