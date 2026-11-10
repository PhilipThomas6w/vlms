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
/// <see cref="Student.Status"/>/<see cref="Student.LeftAt"/>, and marks staff Inactive in the
/// Admin's chapter with an open <see cref="StaffInactivity"/> (FR-017 as amended 27/10/2026) — no self-service path for anyone to
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

    private async Task<StaffInactivity?> OpenPeriodAsync(int userId)
    {
        using var verify = CreateContext(1, Role.Admin);
        return await verify.Context.StaffInactivities.AsNoTracking()
            .SingleOrDefaultAsync(s => s.UserId == userId && s.ChapterId == Chapter.ShrewsburyId && s.ReactivatedAt == null);
    }

    [Fact]
    public async Task MarkInactiveAsync_ByAdmin_OpensAnInactivePeriodInThisChapter_WithTheTimeCapturedAutomatically()
    {
        using var admin = CreateContext(1, Role.Admin);
        var before = DateTime.UtcNow;

        await admin.Service.MarkInactiveAsync(10, CancellationToken.None);

        var after = DateTime.UtcNow;
        var period = await OpenPeriodAsync(10);
        Assert.NotNull(period);
        Assert.InRange(period!.InactiveAt, before, after);
        Assert.Equal(1, period.MarkedInactiveByUserId);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task MarkInactiveAsync_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(2, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Service.MarkInactiveAsync(10, CancellationToken.None));

        Assert.Null(await OpenPeriodAsync(10));
    }

    [Fact]
    public async Task MarkInactiveAsync_NonTeacherAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        // AppUser 20 holds Approver, not Teacher.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkInactiveAsync(20, CancellationToken.None));
    }

    [Fact]
    public async Task MarkInactiveAsync_UnknownAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkInactiveAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task MarkInactiveAsync_AlreadyInactive_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkInactiveAsync(10, CancellationToken.None);
        }

        using var secondAttempt = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondAttempt.Service.MarkInactiveAsync(10, CancellationToken.None));
    }

    // --- FR-017 (amended 27/10/2026): Inactive suspends the chapter's roles; sign-in still works. ---

    [Theory]
    [InlineData(30)] // Admin
    [InlineData(40)] // Safeguarding Officer
    [InlineData(50)] // Teacher who is also a Parent
    public async Task MarkInactiveAsync_AnyStaffRole_OpensAPeriod_RotatesStamp_KeepsRoles_AndDoesNotLockOut(int userId)
    {
        var stampBefore = await CreateSignInAsync(userId);
        var rolesBefore = await RolesOfAsync(userId);

        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkInactiveAsync(userId, CancellationToken.None);
        }

        Assert.NotNull(await OpenPeriodAsync(userId));
        using var verify = CreateContext(1, Role.Admin);
        var identity = await verify.Context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.NotEqual(stampBefore, identity.SecurityStamp);
        Assert.Equal(rolesBefore, await RolesOfAsync(userId));
        Assert.False(await InactiveStaffPolicy.IsLockedOutAsync(verify.Context, userId));
    }

    [Fact]
    public async Task MarkInactiveAsync_Self_Throws_AndChangesNothing()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => admin.Service.MarkInactiveAsync(1, CancellationToken.None));
            Assert.Contains("yourself", ex.Message);
        }

        Assert.Null(await OpenPeriodAsync(1));
    }

    [Fact]
    public async Task MarkInactiveAsync_LastActiveAdmin_Throws()
    {
        // Admin Two is marked Inactive first (allowed: Admin One remains). Then a caller other than
        // Admin One (context user id 99) can't mark Admin One, the last active Admin, Inactive.
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkInactiveAsync(30, CancellationToken.None);
        }

        using (var other = CreateContext(99, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => other.Service.MarkInactiveAsync(1, CancellationToken.None));
            Assert.Contains("last active Admin", ex.Message);
        }

        Assert.Null(await OpenPeriodAsync(1));
    }

    [Fact]
    public async Task MarkInactiveAsync_AdminWhenAnotherActiveAdminExists_Succeeds()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkInactiveAsync(30, CancellationToken.None);
        }

        Assert.NotNull(await OpenPeriodAsync(30));
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
