using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Authorization;

/// <summary>
/// Verifies <see cref="MfaRequirementHandler"/> against the scope decision
/// <see cref="MfaRequirement"/>'s doc comment records (STATE.md "Identity" item 4): satisfied unless
/// the caller holds <see cref="Role.Admin"/> or <see cref="Role.SafeguardingOfficer"/> without
/// <see cref="Vlms.Domain.ICurrentUserContext.HasMfaEnabled"/> — every other role/combination
/// succeeds regardless of MFA status.
/// </summary>
public sealed class MfaRequirementHandlerTests
{
    private static AuthorizationHandlerContext BuildContext() =>
        new([new MfaRequirement()], new ClaimsPrincipal(new ClaimsIdentity()), resource: null);

    [Fact]
    public async Task Succeeds_ForANonMfaGatedRole_RegardlessOfMfaStatus()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: false, Role.Teacher));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_ForACallerWithNoRolesAtAll()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: false));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_ForAdmin_WhenMfaIsEnabled()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: true, Role.Admin));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DoesNotSucceed_ForAdmin_WhenMfaIsNotEnabled()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: false, Role.Admin));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task DoesNotSucceed_ForSafeguardingOfficer_WhenMfaIsNotEnabled()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: false, Role.SafeguardingOfficer));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_ForSafeguardingOfficer_WhenMfaIsEnabled()
    {
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: true, Role.SafeguardingOfficer));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DoesNotSucceed_ForACallerHoldingAdminAlongsideANonGatedRole_WhenMfaIsNotEnabled()
    {
        // The "globally enforced" scope decision under test: holding Teacher too does not exempt
        // an Admin without MFA — MfaRequirement is carried on every policy (Program.cs), including
        // ones a non-gated role like Teacher would otherwise satisfy on its own.
        var handler = new MfaRequirementHandler(new FakeCurrentUserContext(userId: 1, hasMfaEnabled: false, Role.Admin, Role.Teacher));
        var context = BuildContext();

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}
