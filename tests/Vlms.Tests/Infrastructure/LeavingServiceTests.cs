using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="LeavingService"/> (STATE.md "Manual 'leaving' flag for students and
/// teachers, feeding the retention report"): the Admin-only action that sets
/// <see cref="Student.Status"/>/<see cref="Student.LeftAt"/> and
/// <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/> — no self-service path for anyone to
/// mark themselves inactive/left, and no cascading effect on anything else (promotions, badges,
/// certificates, existing <see cref="DbsCheck"/> rows).
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="DbsCheckServiceTests"/>.
/// </summary>
public sealed class LeavingServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public LeavingServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-leaving-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();

        SeedReferenceData(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ICurrentUserContext CurrentUser { get; } = currentUser;

        public LeavingService Service => new(Context, CurrentUser, UserManager);

        public UserManager<VlmsIdentityUser> UserManager => scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();

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

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });

        context.Students.Add(new Student
        {
            Id = 100,
            Name = "Active Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2023, 1, 1)
        });
        context.Students.Add(new Student
        {
            Id = 101,
            Name = "Already Graduated Student",
            DateOfBirth = new DateOnly(2010, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Graduated,
            EnrolmentDate = new DateOnly(2019, 1, 1)
        });

        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin });
        context.AppUsers.Add(new AppUser { Id = 30, DisplayName = "Admin Two", Email = "admin2@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 30, Role = Role.Admin });
        context.AppUsers.Add(new AppUser { Id = 40, DisplayName = "Safeguarding One", Email = "so1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 40, Role = Role.SafeguardingOfficer });
        context.AppUsers.Add(new AppUser { Id = 50, DisplayName = "Teacher Parent", Email = "tp@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 50, Role = Role.Teacher });
        context.UserRoles.Add(new UserRole { UserId = 50, Role = Role.Parent });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Approver One", Email = "approver1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 20, Role = Role.Approver });

        context.SaveChanges();
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_ByAdmin_SetsStatusAndCapturesLeftAtAutomatically()
    {
        using var admin = CreateContext(1, Role.Admin);
        var before = DateTime.UtcNow;

        var student = await admin.Service.MarkStudentInactiveAsync(100, CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.Equal(StudentStatus.Inactive, student.Status);
        Assert.NotNull(student.LeftAt);
        Assert.InRange(student.LeftAt!.Value, before, after);

        using var verify = CreateContext(1, Role.Admin);
        var persisted = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Inactive, persisted.Status);
        Assert.Equal(student.LeftAt, persisted.LeftAt);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task MarkStudentInactiveAsync_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(2, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Service.MarkStudentInactiveAsync(100, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillActive = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Active, stillActive.Status);
        Assert.Null(stillActive.LeftAt);
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_AlreadyGraduatedStudent_Throws_AndChangesNothing()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStudentInactiveAsync(101, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillGraduated = await verify.Context.Students.SingleAsync(s => s.Id == 101);
        Assert.Equal(StudentStatus.Graduated, stillGraduated.Status);
        Assert.Null(stillGraduated.LeftAt);
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_AlreadyInactiveStudent_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStudentInactiveAsync(100, CancellationToken.None);
        }

        using var secondAttempt = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondAttempt.Service.MarkStudentInactiveAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_UnknownStudent_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStudentInactiveAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task MarkStaffLeftAsync_ByAdmin_SetsHasLeftAndCapturesLeftAtAutomatically()
    {
        using var admin = CreateContext(1, Role.Admin);
        var before = DateTime.UtcNow;

        var teacher = await admin.Service.MarkStaffLeftAsync(10, CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.True(teacher.HasLeft);
        Assert.NotNull(teacher.LeftAt);
        Assert.InRange(teacher.LeftAt!.Value, before, after);

        using var verify = CreateContext(1, Role.Admin);
        var persisted = await verify.Context.AppUsers.SingleAsync(u => u.Id == 10);
        Assert.True(persisted.HasLeft);
        Assert.Equal(teacher.LeftAt, persisted.LeftAt);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task MarkStaffLeftAsync_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(2, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Service.MarkStaffLeftAsync(10, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillThere = await verify.Context.AppUsers.SingleAsync(u => u.Id == 10);
        Assert.False(stillThere.HasLeft);
        Assert.Null(stillThere.LeftAt);
    }

    [Fact]
    public async Task MarkStaffLeftAsync_NonTeacherAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        // AppUser 20 holds Approver, not Teacher.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStaffLeftAsync(20, CancellationToken.None));
    }

    [Fact]
    public async Task MarkStaffLeftAsync_UnknownAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStaffLeftAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task MarkStaffLeftAsync_AlreadyLeft_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStaffLeftAsync(10, CancellationToken.None);
        }

        using var secondAttempt = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondAttempt.Service.MarkStaffLeftAsync(10, CancellationToken.None));
    }

    // --- FR-017 (amended 22/10/2026): staff who leave are locked out entirely. ---

    [Theory]
    [InlineData(30)] // Admin
    [InlineData(40)] // Safeguarding Officer
    [InlineData(50)] // Teacher who is also a Parent
    public async Task MarkStaffLeftAsync_AnyStaffRole_SetsHasLeft_RotatesStamp_KeepsRoles(int userId)
    {
        var stampBefore = await CreateSignInAsync(userId);
        var rolesBefore = await RolesOfAsync(userId);

        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStaffLeftAsync(userId, CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.True((await verify.Context.AppUsers.SingleAsync(u => u.Id == userId)).HasLeft);
        var identity = await verify.Context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.NotEqual(stampBefore, identity.SecurityStamp);
        Assert.Equal(rolesBefore, await RolesOfAsync(userId));
        Assert.False(LeftStaffPolicy.StaffRoleCounts(verify.Context, userId));
        // Locked out of sign-in only with no other role: the Teacher who is also a Parent (50) keeps it.
        Assert.Equal(userId != 50, await LeftStaffPolicy.IsLockedOutAsync(verify.Context, userId));
    }

    [Fact]
    public async Task MarkStaffLeftAsync_Self_Throws_AndChangesNothing()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => admin.Service.MarkStaffLeftAsync(1, CancellationToken.None));
            Assert.Contains("yourself", ex.Message);
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.False((await verify.Context.AppUsers.SingleAsync(u => u.Id == 1)).HasLeft);
    }

    [Fact]
    public async Task MarkStaffLeftAsync_LastAdminWhoHasNotLeft_Throws()
    {
        // Admin Two leaves first (allowed: Admin One remains). Then a caller other than Admin One
        // (context user id 99) can't mark Admin One, the last working Admin, as left.
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStaffLeftAsync(30, CancellationToken.None);
        }

        using (var other = CreateContext(99, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => other.Service.MarkStaffLeftAsync(1, CancellationToken.None));
            Assert.Contains("last Admin", ex.Message);
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.False((await verify.Context.AppUsers.SingleAsync(u => u.Id == 1)).HasLeft);
    }

    [Fact]
    public async Task MarkStaffLeftAsync_AdminWhenAnotherWorkingAdminExists_Succeeds()
    {
        using var admin = CreateContext(1, Role.Admin);

        var left = await admin.Service.MarkStaffLeftAsync(30, CancellationToken.None);

        Assert.True(left.HasLeft);
    }

    [Fact]
    public async Task LockOut_HoldsEvenAfterTheLeaversRolesAreRemoved()
    {
        await CreateSignInAsync(10);
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStaffLeftAsync(10, CancellationToken.None);
            admin.Context.UserRoles.RemoveRange(admin.Context.UserRoles.Where(r => r.UserId == 10));
            await admin.Context.SaveChangesAsync();
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.True(await LeftStaffPolicy.IsLockedOutAsync(verify.Context, 10));
    }

    [Fact]
    public async Task LockOut_AppliesToAStaffLeaverWhoseFlagWasSetDirectly()
    {
        await CreateSignInAsync(10);
        using (var admin = CreateContext(1, Role.Admin))
        {
            var user = await admin.Context.AppUsers.SingleAsync(u => u.Id == 10);
            admin.Context.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            await admin.Context.SaveChangesAsync();
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.True(await LeftStaffPolicy.IsLockedOutAsync(verify.Context, 10));
        Assert.False(await LeftStaffPolicy.IsLockedOutAsync(verify.Context, 20));
    }

    [Fact]
    public async Task LockOut_DoesNotApplyToAParentOrApproverWhoHasLeft()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            admin.Context.AppUsers.Add(new AppUser { Id = 60, DisplayName = "Parent Left", Email = "p60@example.com", HasLeft = true, LeftAt = DateTime.UtcNow });
            admin.Context.UserRoles.Add(new UserRole { UserId = 60, Role = Role.Parent });
            var approver = await admin.Context.AppUsers.SingleAsync(u => u.Id == 20);
            admin.Context.Entry(approver).CurrentValues[nameof(AppUser.HasLeft)] = true;
            await admin.Context.SaveChangesAsync();
        }

        using var verify = CreateContext(1, Role.Admin);
        Assert.False(await LeftStaffPolicy.IsLockedOutAsync(verify.Context, 60));
        Assert.False(await LeftStaffPolicy.IsLockedOutAsync(verify.Context, 20));
    }

    /// <summary>Creates the Identity sign-in paired with an existing AppUser; returns its security stamp.</summary>
    private async Task<string?> CreateSignInAsync(int userId)
    {
        using var caller = CreateContext(1, Role.Admin);
        var appUser = await caller.Context.AppUsers.SingleAsync(u => u.Id == userId);
        var identity = new VlmsIdentityUser { Id = userId, UserName = appUser.Email, Email = appUser.Email, EmailConfirmed = true };
        var created = await caller.UserManager.CreateAsync(identity, "Str0ng!Passw0rd");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return identity.SecurityStamp;
    }

    private async Task<List<Role>> RolesOfAsync(int userId)
    {
        using var verify = CreateContext(1, Role.Admin);
        return await verify.Context.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role).OrderBy(r => r).ToListAsync();
    }
}
