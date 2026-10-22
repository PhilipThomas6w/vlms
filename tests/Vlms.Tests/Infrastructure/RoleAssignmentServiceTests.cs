using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="RoleAssignmentService"/> (STATE.md "Admin role assignment + first-Admin
/// bootstrap"): Admin-only grant/revoke of <see cref="UserRole"/> rows, the last-Admin lockout
/// guard, and the no-roles-for-leavers guard.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LeavingServiceTests"/>.
/// </summary>
public sealed class RoleAssignmentServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public RoleAssignmentServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-role-assignment-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();

        schema.Context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        schema.Context.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin });
        schema.Context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "New User", Email = "new@example.com" });
        schema.Context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Teacher One", Email = "teacher@example.com" });
        schema.Context.UserRoles.Add(new UserRole { UserId = 20, Role = Role.Teacher });
        schema.Context.AppUsers.Add(new AppUser
        {
            Id = 30, DisplayName = "Left Teacher", Email = "left@example.com", HasLeft = true, LeftAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        schema.Context.UserRoles.Add(new UserRole { UserId = 30, Role = Role.Teacher });
        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public RoleAssignmentService Service => new(Context, currentUser, scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(int? userId, params Role[] roles)
    {
        var currentUser = new FakeCurrentUserContext(userId, roles);

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        services.AddLogging();
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context, currentUser);
    }

    private async Task<List<Role>> RolesOf(int userId)
    {
        using var verify = CreateContext(1, Role.Admin);
        return await verify.Context.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role).ToListAsync();
    }

    [Fact]
    public async Task GrantAsync_ByAdmin_PersistsUserRoleRow()
    {
        using var admin = CreateContext(1, Role.Admin);

        await admin.Service.GrantAsync(10, Role.Parent);

        Assert.Equal([Role.Parent], await RolesOf(10));
    }

    [Fact]
    public async Task GrantAsync_SecondRole_KeepsExistingRole()
    {
        using var admin = CreateContext(1, Role.Admin);

        await admin.Service.GrantAsync(20, Role.Approver);

        Assert.Equal([Role.Teacher, Role.Approver], (await RolesOf(20)).OrderBy(r => r));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task EveryMethod_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(20, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListUsersAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.GrantAsync(20, Role.Admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.RevokeAsync(1, Role.Admin));

        Assert.Equal([Role.Teacher], await RolesOf(20));
        Assert.Equal([Role.Admin], await RolesOf(1));
    }

    [Fact]
    public async Task GrantAsync_ByCallerWithNoRoles_Throws()
    {
        using var caller = CreateContext(10);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.GrantAsync(10, Role.Admin));
        Assert.Empty(await RolesOf(10));
    }

    [Fact]
    public async Task GrantAsync_RoleAlreadyHeld_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.GrantAsync(20, Role.Teacher));
        Assert.Equal([Role.Teacher], await RolesOf(20));
    }

    [Fact]
    public async Task GrantAsync_ToLeaver_Throws_AndGrantsNothing()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.GrantAsync(30, Role.Approver));
        Assert.Equal([Role.Teacher], await RolesOf(30));
    }

    [Fact]
    public async Task GrantAsync_UnknownUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.GrantAsync(999, Role.Teacher));
    }

    [Fact]
    public async Task RevokeAsync_FromLeaver_IsAllowed()
    {
        using var admin = CreateContext(1, Role.Admin);

        await admin.Service.RevokeAsync(30, Role.Teacher);

        Assert.Empty(await RolesOf(30));
    }

    [Fact]
    public async Task RevokeAsync_RoleNotHeld_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.RevokeAsync(10, Role.Teacher));
    }

    [Fact]
    public async Task RevokeAsync_LastAdmin_Throws_AndAdminSurvives()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.RevokeAsync(1, Role.Admin));
        Assert.Equal([Role.Admin], await RolesOf(1));
    }

    [Fact]
    public async Task RevokeAsync_AdminWhenAnotherAdminExists_Succeeds()
    {
        using var admin = CreateContext(1, Role.Admin);
        await admin.Service.GrantAsync(20, Role.Admin);

        await admin.Service.RevokeAsync(1, Role.Admin);

        Assert.Empty(await RolesOf(1));
        Assert.Contains(Role.Admin, await RolesOf(20));
    }

    [Fact]
    public async Task ListUsersAsync_ByAdmin_ReturnsEveryUserWithTheirRoles()
    {
        using var admin = CreateContext(1, Role.Admin);

        var users = await admin.Service.ListUsersAsync();

        Assert.Equal(["Admin One", "Left Teacher", "New User", "Teacher One"], users.Select(u => u.DisplayName));
        Assert.Empty(users.Single(u => u.UserId == 10).Roles);
        Assert.Equal([Role.Teacher], users.Single(u => u.UserId == 20).Roles);
        Assert.True(users.Single(u => u.UserId == 30).HasLeft);
    }

    /// <summary>
    /// Adds a guardian (AppUser 40) who accepted an invite for "Student A", and a self-registered
    /// leaver with no roles (AppUser 50) who must never appear as waiting.
    /// </summary>
    private void SeedAcceptedGuardianInvite()
    {
        using var admin = CreateContext(1, Role.Admin);
        var db = admin.Context;
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Students.Add(new Student
        {
            Id = 100, Name = "Student A", DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 9, 1)
        });
        db.AppUsers.Add(new AppUser { Id = 40, DisplayName = "Guardian Gail", Email = "gail@example.com" });
        db.AppUsers.Add(new AppUser
        {
            Id = 50, DisplayName = "Roleless Leaver", Email = "gone@example.com", HasLeft = true, LeftAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.ParentGuardians.Add(new ParentGuardian { Id = 200, Name = "Guardian Gail", ContactInfo = "gail@example.com", IsPrimary = true, AppUserId = 40 });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 200, CreatedByUserId = 20 });
        db.GuardianInvites.Add(new GuardianInvite
        {
            Id = 300, StudentId = 100, ParentGuardianId = 200, CreatedByUserId = 20,
            CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
            ConsumedAt = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            ConsumedByAppUserId = 40
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ListAwaitingApprovalAsync_ReturnsOnlyRolelessNonLeavers_WithInviteOriginAndLinkedStudents()
    {
        SeedAcceptedGuardianInvite();
        using var admin = CreateContext(1, Role.Admin);

        var waiting = await admin.Service.ListAwaitingApprovalAsync();

        Assert.Equal([40, 10], waiting.Select(w => w.UserId));
        var gail = waiting[0];
        Assert.True(gail.FromGuardianInvite);
        Assert.Equal(["Student A"], gail.LinkedStudentNames);
        var selfRegistered = waiting[1];
        Assert.False(selfRegistered.FromGuardianInvite);
        Assert.Empty(selfRegistered.LinkedStudentNames);
    }

    [Fact]
    public async Task ApproveGuardianAsParentAsync_InviteAccount_GrantsParent_AndLeavesTheWaitingList()
    {
        SeedAcceptedGuardianInvite();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Service.ApproveGuardianAsParentAsync(40);

        Assert.Equal([Role.Parent], await RolesOf(40));
        using var verify = CreateContext(1, Role.Admin);
        Assert.DoesNotContain(40, (await verify.Service.ListAwaitingApprovalAsync()).Select(w => w.UserId));
    }

    [Fact]
    public async Task ApproveGuardianAsParentAsync_SelfRegisteredAccount_Throws_AndGrantsNothing()
    {
        SeedAcceptedGuardianInvite();
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ApproveGuardianAsParentAsync(10));
        Assert.Empty(await RolesOf(10));
    }

    [Fact]
    public async Task ApproveGuardianAsParentAsync_AlreadyParent_Throws()
    {
        SeedAcceptedGuardianInvite();
        using var admin = CreateContext(1, Role.Admin);
        await admin.Service.ApproveGuardianAsParentAsync(40);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ApproveGuardianAsParentAsync(40));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task ApprovalMethods_ByNonAdminRole_Throw_AndGrantNothing(Role role)
    {
        SeedAcceptedGuardianInvite();
        using var caller = CreateContext(20, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListAwaitingApprovalAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ApproveGuardianAsParentAsync(40));

        Assert.Empty(await RolesOf(40));
    }

    // FR-011, adr/0007: only a parent creating a student sign-in grants Student.
    [Fact]
    public async Task GrantAsync_StudentRole_IsRefused_ToAnyAccount()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.GrantAsync(10, Role.Student));

        Assert.Empty(await RolesOf(10));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Parent)]
    public async Task GrantAsync_AnyRole_ToAStudentSignIn_IsRefused(Role role)
    {
        using (var seed = CreateContext(1, Role.Admin))
        {
            seed.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            seed.Context.AppUsers.Add(new AppUser { Id = 50, DisplayName = "Kid", Email = "" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 50, Role = Role.Student });
            seed.Context.Students.Add(new Student
            {
                Id = 500, Name = "Kid", DateOfBirth = new DateOnly(2016, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 1, 1), AppUserId = 50
            });
            seed.Context.SaveChanges();
        }

        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.GrantAsync(50, role));

        Assert.Equal([Role.Student], await RolesOf(50));
    }
}
