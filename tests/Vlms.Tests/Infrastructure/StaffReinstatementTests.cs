using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// functional.md FR-017 as amended 27/10/2026: staff step down and are reinstated
/// (<see cref="LeavingService.StepDownAsync"/>, <see cref="LeavingService.ReinstateAsync"/>).
/// Admin only, own chapter, not self; roles restored exactly and only those held; the lockout while
/// stepped down and its lifting; the MFA rule; the retention clock; parent approval while stepped
/// down; and the history rows. Same SQLite-in-memory-via-DI pattern as <see cref="LeavingServiceTests"/>.
/// </summary>
public sealed class StaffReinstatementTests : IDisposable
{
    private const int AdminOne = 1;
    private const int AdminTwo = 30;
    private const int Safeguarding = 40;
    private const int TeacherParent = 50;
    private const int Teacher = 10;
    private const int Coventry = 2;

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public StaffReinstatementTests()
    {
        _connectionString = $"Data Source=file:vlms-reinstate-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var seed = Caller(AdminOne, Role.Admin);
        seed.Db.Database.EnsureCreated();
        seed.Db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        void Staff(int id, string name, params Role[] roles)
        {
            seed.Db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
            foreach (var role in roles)
            {
                seed.Db.UserRoles.Add(new UserRole { UserId = id, Role = role });
            }
        }

        Staff(AdminOne, "Admin One", Role.Admin);
        Staff(AdminTwo, "Admin Two", Role.Admin, Role.Teacher);
        Staff(Safeguarding, "Safeguarding One", Role.SafeguardingOfficer);
        Staff(TeacherParent, "Teacher Parent", Role.Teacher, Role.Parent);
        Staff(Teacher, "Teacher One", Role.Teacher);
        seed.Db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private sealed class Scope(ServiceProvider provider, IServiceScope scope, VlmsDbContext db, ICurrentUserContext user) : IDisposable
    {
        public VlmsDbContext Db { get; } = db;

        public UserManager<VlmsIdentityUser> Users => scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();

        public LeavingService Leaving => new(Db, user, Users);

        public RoleAssignmentService Roles => new(Db, user, Users);

        public void Dispose()
        {
            Db.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Scope Caller(int? userId, params Role[] roles) => Caller(new FakeCurrentUserContext(userId, roles));

    private Scope Caller(ICurrentUserContext user)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(user);
        services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(_connectionString));
        services.AddLogging();
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new Scope(provider, scope, scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), user);
    }

    private async Task StepDownAsync(int userId, int by = AdminOne)
    {
        using var admin = Caller(by, Role.Admin);
        await admin.Leaving.StepDownAsync(userId);
    }

    private async Task ReinstateAsync(int userId, int by = AdminTwo)
    {
        using var admin = Caller(by, Role.Admin);
        await admin.Leaving.ReinstateAsync(userId);
    }

    private List<(Role Role, int? ChapterId)> RolesOf(int userId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return verify.Db.UserRoles.Where(r => r.UserId == userId).OrderBy(r => r.Role)
            .Select(r => new { r.Role, r.ChapterId }).AsEnumerable().Select(r => (r.Role, r.ChapterId)).ToList();
    }

    private AppUser User(int userId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return verify.Db.AppUsers.AsNoTracking().Single(u => u.Id == userId);
    }

    private List<StaffStepDown> HistoryOf(int userId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return verify.Db.StaffStepDowns.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Id).ToList();
    }

    private async Task<VlmsIdentityUser> SignInFor(int userId, bool withAuthenticator = true)
    {
        using var scope = Caller(AdminOne, Role.Admin);
        var identity = new VlmsIdentityUser
        {
            Id = userId, UserName = $"user{userId}@example.com", Email = $"user{userId}@example.com", EmailConfirmed = true
        };
        Assert.True((await scope.Users.CreateAsync(identity, "Str0ng!Passw0rd")).Succeeded);
        if (withAuthenticator)
        {
            await scope.Users.ResetAuthenticatorKeyAsync(identity);
            await scope.Users.SetTwoFactorEnabledAsync(identity, true);
        }

        return identity;
    }

    private async Task<VlmsIdentityUser> IdentityOf(int userId)
    {
        using var scope = Caller(AdminOne, Role.Admin);
        return await scope.Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    [Theory]
    [InlineData(AdminTwo)]       // Admin and Teacher
    [InlineData(Safeguarding)]
    [InlineData(TeacherParent)]
    public async Task Reinstate_RestoresExactlyTheRolesHeld_AndLiftsTheLockout_AndRotatesTheStamp(int userId)
    {
        await SignInFor(userId);
        var rolesBefore = RolesOf(userId);
        await StepDownAsync(userId);
        var stampWhileSteppedDown = (await IdentityOf(userId)).SecurityStamp;
        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.False(LeftStaffPolicy.StaffRoleCounts(check.Db, userId));
            Assert.Equal(userId != TeacherParent, await LeftStaffPolicy.IsLockedOutAsync(check.Db, userId));
        }

        var reinstater = userId == AdminTwo ? AdminOne : AdminTwo;
        await ReinstateAsync(userId, by: reinstater);

        Assert.Equal(rolesBefore, RolesOf(userId));
        var user = User(userId);
        Assert.False(user.HasLeft);
        Assert.Null(user.LeftAt);
        Assert.NotEqual(stampWhileSteppedDown, (await IdentityOf(userId)).SecurityStamp);
        using var verify = Caller(AdminOne, Role.Admin);
        Assert.True(LeftStaffPolicy.StaffRoleCounts(verify.Db, userId));
        Assert.False(await LeftStaffPolicy.IsLockedOutAsync(verify.Db, userId));
    }

    [Fact]
    public async Task Reinstate_KeepsTheAuthenticator_AndTheMfaRuleAppliesAgain()
    {
        var before = await SignInFor(Teacher);
        await StepDownAsync(Teacher);
        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.False(LeftStaffPolicy.HoldsCountingStaffRoleInAnyChapter(check.Db, Teacher));
        }

        await ReinstateAsync(Teacher);

        using var verify = Caller(AdminOne, Role.Admin);
        Assert.True(LeftStaffPolicy.HoldsCountingStaffRoleInAnyChapter(verify.Db, Teacher));
        var identity = await verify.Users.FindByIdAsync(Teacher.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(await verify.Users.GetTwoFactorEnabledAsync(identity!));
        Assert.Equal(await AuthenticatorKeyOf(before), await verify.Users.GetAuthenticatorKeyAsync(identity!));
        Assert.True(await verify.Users.CheckPasswordAsync(identity!, "Str0ng!Passw0rd"));
    }

    private async Task<string?> AuthenticatorKeyOf(VlmsIdentityUser user)
    {
        using var scope = Caller(AdminOne, Role.Admin);
        var found = await scope.Users.FindByIdAsync(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await scope.Users.GetAuthenticatorKeyAsync(found!);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.NationalAdministrator)]
    public async Task Reinstate_ByANonAdmin_IsRefused_AndChangesNothing(Role role)
    {
        await StepDownAsync(Teacher);

        using (var caller = Caller(Safeguarding, role))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Leaving.ReinstateAsync(Teacher));
        }

        Assert.True(User(Teacher).HasLeft);
        Assert.Null(Assert.Single(HistoryOf(Teacher)).ReinstatedAt);
    }

    [Fact]
    public async Task Reinstate_Self_IsRefused()
    {
        await StepDownAsync(AdminTwo);

        using (var self = Caller(AdminTwo, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => self.Leaving.ReinstateAsync(AdminTwo));
            Assert.Contains("yourself", ex.Message);
        }

        Assert.True(User(AdminTwo).HasLeft);
    }

    [Fact]
    public async Task Reinstate_ByAnotherChaptersAdmin_IsRefused()
    {
        await StepDownAsync(Teacher);

        using (var coventry = Caller(new FakeCurrentUserContext(AdminTwo, Role.Admin) { ActiveChapterId = Coventry }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coventry.Leaving.ReinstateAsync(Teacher));
        }

        Assert.True(User(Teacher).HasLeft);
    }

    [Fact]
    public async Task Reinstate_WithStaffRolesInTwoChapters_IsRefused()
    {
        // Not reachable through the app (stepping down refuses staff of two chapters); set directly.
        using (var seed = Caller(new FakeCurrentUserContext(null) { SeesAllChapters = true, ActiveChapterId = null }))
        {
            seed.Db.UserRoles.Add(new UserRole { UserId = Teacher, Role = Role.Teacher, ChapterId = Coventry });
            var user = seed.Db.AppUsers.Single(u => u.Id == Teacher);
            seed.Db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            seed.Db.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = DateTime.UtcNow;
            await seed.Db.SaveChangesAsync();
        }

        using var admin = Caller(AdminOne, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReinstateAsync(Teacher));
    }

    [Fact]
    public async Task Reinstate_SomeoneWhoHasNotSteppedDown_OrHoldsNoStaffRole_IsRefused()
    {
        using var admin = Caller(AdminOne, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReinstateAsync(Teacher));

        // A parent who left (flag set directly; no service does this) is not staff to reinstate.
        admin.Db.AppUsers.Add(new AppUser { Id = 60, DisplayName = "Parent Left", Email = "p60@example.com", HasLeft = true, LeftAt = DateTime.UtcNow });
        admin.Db.UserRoles.Add(new UserRole { UserId = 60, Role = Role.Parent });
        await admin.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReinstateAsync(60));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReinstateAsync(9999));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task WhileSteppedDown_NoStaffRoleCanBeAdded_SoReinstatementRestoresOnlyThoseHeld(Role role)
    {
        await StepDownAsync(Safeguarding);

        using (var admin = Caller(AdminOne, Role.Admin))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Roles.GrantAsync(Safeguarding, role));
        }

        await ReinstateAsync(Safeguarding);
        Assert.Equal([(Role.SafeguardingOfficer, (int?)Chapter.ShrewsburyId)], RolesOf(Safeguarding));
    }

    [Fact]
    public async Task WhileSteppedDown_ParentCanBeGranted_TheyCanSignInAsAParent_AndStaffRolesStayOff()
    {
        await SignInFor(Teacher, withAuthenticator: false);
        await StepDownAsync(Teacher);

        using (var admin = Caller(AdminOne, Role.Admin))
        {
            await admin.Roles.GrantAsync(Teacher, Role.Parent);
        }

        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.False(await LeftStaffPolicy.IsLockedOutAsync(check.Db, Teacher));
            Assert.False(LeftStaffPolicy.StaffRoleCounts(check.Db, Teacher));
            Assert.False(LeftStaffPolicy.HoldsCountingStaffRoleInAnyChapter(check.Db, Teacher));
        }

        await ReinstateAsync(Teacher);
        Assert.Equal(
            [(Role.Teacher, (int?)Chapter.ShrewsburyId), (Role.Parent, (int?)Chapter.ShrewsburyId)],
            RolesOf(Teacher));
    }

    [Fact]
    public async Task SteppedDownGuardianFromAnInvite_IsListedAsWaiting_AndCanBeApprovedAsParent()
    {
        await StepDownAsync(Teacher);
        using (var seed = Caller(AdminOne, Role.Admin))
        {
            seed.Db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            seed.Db.Students.Add(new Student
            {
                Id = 100, Name = "Student A", DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 9, 1)
            });
            seed.Db.ParentGuardians.Add(new ParentGuardian { Id = 200, Name = "Teacher One", ContactInfo = "x", IsPrimary = true, AppUserId = Teacher });
            seed.Db.GuardianInvites.Add(new GuardianInvite
            {
                Id = 300, StudentId = 100, ParentGuardianId = 200, CreatedByUserId = AdminOne,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), ExpiresAt = DateTimeOffset.UtcNow.AddDays(5),
                ConsumedAt = DateTimeOffset.UtcNow.AddDays(-1), ConsumedByAppUserId = Teacher
            });
            await seed.Db.SaveChangesAsync();
        }

        using var admin = Caller(AdminOne, Role.Admin);
        Assert.Contains(Teacher, (await admin.Roles.ListAwaitingApprovalAsync()).Select(w => w.UserId));
        await admin.Roles.ApproveGuardianAsParentAsync(Teacher);

        Assert.Contains((Role.Parent, (int?)Chapter.ShrewsburyId), RolesOf(Teacher));
        Assert.True(User(Teacher).HasLeft);
    }

    [Fact]
    public async Task History_KeepsEachPeriod_WithWhoActedAndWhen()
    {
        var start = DateTime.UtcNow;
        await StepDownAsync(Teacher, by: AdminOne);
        await ReinstateAsync(Teacher, by: AdminTwo);
        await StepDownAsync(Teacher, by: AdminTwo);
        await ReinstateAsync(Teacher, by: AdminOne);
        var end = DateTime.UtcNow;

        var history = HistoryOf(Teacher);
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(Chapter.ShrewsburyId, h.ChapterId));
        Assert.Equal((AdminOne, AdminTwo), (history[0].SteppedDownByUserId!.Value, history[0].ReinstatedByUserId!.Value));
        Assert.Equal((AdminTwo, AdminOne), (history[1].SteppedDownByUserId!.Value, history[1].ReinstatedByUserId!.Value));
        Assert.All(history, h =>
        {
            Assert.InRange(h.SteppedDownAt, start, end);
            Assert.InRange(h.ReinstatedAt!.Value, h.SteppedDownAt, end);
        });
        Assert.True(history[1].SteppedDownAt >= history[0].ReinstatedAt);

        using var admin = Caller(AdminOne, Role.Admin);
        var member = Assert.Single(await admin.Leaving.ListStaffAsync(), m => m.UserId == Teacher);
        Assert.False(member.SteppedDown);
        Assert.Equal(["Admin One", "Admin Two"], member.History.Select(p => p.SteppedDownBy));
        Assert.Equal(["Admin Two", "Admin One"], member.History.Select(p => p.ReinstatedBy));
    }

    [Fact]
    public async Task StepDown_OpensAHistoryRow_ListedAsSteppedDown()
    {
        await StepDownAsync(Teacher);

        var row = Assert.Single(HistoryOf(Teacher));
        Assert.Equal(AdminOne, row.SteppedDownByUserId);
        Assert.Null(row.ReinstatedAt);
        Assert.Equal(User(Teacher).LeftAt, row.SteppedDownAt);

        using var admin = Caller(AdminOne, Role.Admin);
        var member = Assert.Single(await admin.Leaving.ListStaffAsync(), m => m.UserId == Teacher);
        Assert.True(member.SteppedDown);
        Assert.Equal([Role.Teacher], member.Roles);
    }

    [Fact]
    public async Task Reinstate_SomeoneSteppedDownBeforeTheHistoryExisted_RecordsTheirPeriod()
    {
        var leftAt = DateTime.UtcNow.AddYears(-1);
        using (var seed = Caller(AdminOne, Role.Admin))
        {
            var user = seed.Db.AppUsers.Single(u => u.Id == Teacher);
            seed.Db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            seed.Db.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = leftAt;
            await seed.Db.SaveChangesAsync();
        }

        await ReinstateAsync(Teacher);

        var row = Assert.Single(HistoryOf(Teacher));
        Assert.Equal(leftAt, row.SteppedDownAt);
        Assert.Null(row.SteppedDownByUserId);
        Assert.Equal(AdminTwo, row.ReinstatedByUserId);
    }

    [Fact]
    public async Task ListStaff_IsAdminOnly_AndShowsThisChapterOnly()
    {
        using (var teacher = Caller(Teacher, Role.Teacher))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Leaving.ListStaffAsync());
        }

        using var coventry = Caller(new FakeCurrentUserContext(AdminTwo, Role.Admin) { ActiveChapterId = Coventry });
        Assert.Empty(await coventry.Leaving.ListStaffAsync());
    }

    // --- Retention clock (docs/adr/0014-retention-purge.md decision 3) ---

    private async Task AddDbsCheckAsync(int teacherId, DateOnly expiry)
    {
        using var seed = Caller(AdminOne, Role.Admin);
        seed.Db.DbsChecks.Add(new DbsCheck
        {
            Id = 70, TeacherUserId = teacherId, CheckDate = expiry.AddYears(-3), ExpiryDate = expiry,
            CertificateNumber = "001234567890", Status = DbsCheckStatus.Clear
        });
        await seed.Db.SaveChangesAsync();
    }

    private async Task BackdateStepDownAsync(int userId, DateTime leftAt)
    {
        using var seed = Caller(AdminOne, Role.Admin);
        var user = seed.Db.AppUsers.Single(u => u.Id == userId);
        seed.Db.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = leftAt;
        await seed.Db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DbsCheckDueForDeletion>> DbsDueAsync()
    {
        using var admin = Caller(AdminOne, Role.Admin);
        return await RetentionFlagging.GetDbsChecksDueForDeletionAsync(admin.Db, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task SteppingDownStartsTheClock_ReinstatingStopsIt_AndSteppingDownAgainRestartsItFromThen()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddDbsCheckAsync(Teacher, today.AddYears(-1));
        await StepDownAsync(Teacher);
        await BackdateStepDownAsync(Teacher, DateTime.UtcNow.AddYears(-7));
        Assert.Equal(DbsCheckDeletionReason.TeacherLeft, Assert.Single(await DbsDueAsync()).Reason);

        await ReinstateAsync(Teacher);
        Assert.Empty(await DbsDueAsync());

        await StepDownAsync(Teacher);
        Assert.Empty(await DbsDueAsync());   // the clock runs from the current step-down, today
        Assert.InRange(User(Teacher).LeftAt!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
    }

    [Fact]
    public async Task ReinstatedTeacherWithAnExpiredDbsCheck_IsReinstated_AndFlaggedAsExpired()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddDbsCheckAsync(Teacher, today.AddDays(-10));
        await StepDownAsync(Teacher);

        await ReinstateAsync(Teacher);

        Assert.False(User(Teacher).HasLeft);
        using var job = Caller(Vlms.Infrastructure.Security.SystemCurrentUserContext.Instance);
        var result = await new Vlms.Infrastructure.Safeguarding.ConsentExpiryJob(
            job.Db, Vlms.Infrastructure.Security.SystemCurrentUserContext.Instance,
            new ListLogger<Vlms.Infrastructure.Safeguarding.ConsentExpiryJob>()).RunAsync();
        var flag = Assert.Single(result.DbsFlags, f => f.TeacherUserId == Teacher);
        Assert.True(flag.IsExpired);
        Assert.Equal(70, flag.DbsCheckId);
    }
}
