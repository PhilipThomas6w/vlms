using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Authorization;

/// <summary>Verifies <see cref="RoleAuthorizationHandler"/> succeeds a <see cref="RoleRequirement"/>
/// only when the current caller actually holds that role.</summary>
public sealed class RoleAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext BuildContext(RoleRequirement requirement) =>
        new([requirement], new ClaimsPrincipal(new ClaimsIdentity()), resource: null);

    [Fact]
    public async Task Succeeds_WhenCallerHoldsTheRequiredRole()
    {
        var handler = new RoleAuthorizationHandler(new FakeCurrentUserContext(userId: 1, Role.Admin));
        var context = BuildContext(new RoleRequirement(Role.Admin));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerLacksTheRequiredRole()
    {
        var handler = new RoleAuthorizationHandler(new FakeCurrentUserContext(userId: 1, Role.Teacher));
        var context = BuildContext(new RoleRequirement(Role.Admin));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}
