using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// functional.md FR-017 as amended 27/10/2026 and 03/11/2026: staff are marked Inactive in one
/// chapter and reactivated there (<see cref="LeavingService.MarkInactiveAsync"/>,
/// <see cref="LeavingService.ReactivateAsync"/>). Admin only, own chapter, not self; roles restored
/// exactly; no effect on another chapter; roles granted while Inactive give nothing until
/// reactivation; the MFA rule still applies while Inactive; the retention clock; parent approval
/// while Inactive; and the history rows. Same SQLite-in-memory-via-DI pattern as <see cref="LeavingServiceTests"/>.
/// </summary>
public sealed class StaffReactivationTests : IDisposable
{
    private const int AdminOne = 1;
    private const int AdminTwo = 30;
    private const int Safeguarding = 40;
    private const int TeacherParent = 50;
    private const int Teacher = 10;
    private const int Coventry = 2;

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public StaffReactivationTests()
    {
        _connectionString = $"Data Source=file:vlms-reactivate-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

    private async Task MarkInactiveAsync(int userId, int by = AdminOne)
    {
        using var admin = Caller(by, Role.Admin);
        await admin.Leaving.MarkInactiveAsync(userId);
    }

    private async Task ReactivateAsync(int userId, int by = AdminTwo)
    {
        using var admin = Caller(by, Role.Admin);
        await admin.Leaving.ReactivateAsync(userId);
    }

    private List<(Role Role, int? ChapterId)> RolesOf(int userId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return verify.Db.UserRoles.Where(r => r.UserId == userId).OrderBy(r => r.Role)
            .Select(r => new { r.Role, r.ChapterId }).AsEnumerable().Select(r => (r.Role, r.ChapterId)).ToList();
    }

    private bool IsInactive(int userId, int chapterId = Chapter.ShrewsburyId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return ChapterMembership.IsInactiveInChapter(verify.Db, userId, chapterId);
    }

    private List<StaffInactivity> HistoryOf(int userId)
    {
        using var verify = Caller(AdminOne, Role.Admin);
        return verify.Db.StaffInactivities.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.Id).ToList();
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
    public async Task Reactivate_RestoresExactlyTheRolesHeld_AndLiftsTheLockout_AndRotatesTheStamp(int userId)
    {
        await SignInFor(userId);
        var rolesBefore = RolesOf(userId);
        await MarkInactiveAsync(userId);
        var stampWhileInactive = (await IdentityOf(userId)).SecurityStamp;
        Assert.True(IsInactive(userId));

        var reactivator = userId == AdminTwo ? AdminOne : AdminTwo;
        await ReactivateAsync(userId, by: reactivator);

        Assert.Equal(rolesBefore, RolesOf(userId));
        Assert.False(IsInactive(userId));
        Assert.NotNull(Assert.Single(HistoryOf(userId)).ReactivatedAt);
        Assert.NotEqual(stampWhileInactive, (await IdentityOf(userId)).SecurityStamp);
    }

    [Fact]
    public async Task Reactivate_KeepsTheAuthenticator_AndTheMfaRuleAppliesThroughout()
    {
        // adr/0010 assumption 9 (amended 03/11/2026): kept staff roles still count for MFA while Inactive.
        var before = await SignInFor(Teacher);
        await MarkInactiveAsync(Teacher);
        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.True(InactiveStaffPolicy.HoldsCountingStaffRoleInAnyChapter(check.Db, Teacher));
        }

        await ReactivateAsync(Teacher);

        using var verify = Caller(AdminOne, Role.Admin);
        Assert.True(InactiveStaffPolicy.HoldsCountingStaffRoleInAnyChapter(verify.Db, Teacher));
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
    public async Task Reactivate_ByANonAdmin_IsRefused_AndChangesNothing(Role role)
    {
        await MarkInactiveAsync(Teacher);

        using (var caller = Caller(Safeguarding, role))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Leaving.ReactivateAsync(Teacher));
        }

        Assert.True(IsInactive(Teacher));
        Assert.Null(Assert.Single(HistoryOf(Teacher)).ReactivatedAt);
    }

    [Fact]
    public async Task Reactivate_Self_IsRefused()
    {
        await MarkInactiveAsync(AdminTwo);

        using (var self = Caller(AdminTwo, Role.Admin))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => self.Leaving.ReactivateAsync(AdminTwo));
            Assert.Contains("yourself", ex.Message);
        }

        Assert.True(IsInactive(AdminTwo));
    }

    [Fact]
    public async Task Reactivate_ByAnotherChaptersAdmin_IsRefused()
    {
        await MarkInactiveAsync(Teacher);

        using (var coventry = Caller(new FakeCurrentUserContext(AdminTwo, Role.Admin) { ActiveChapterId = Coventry }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coventry.Leaving.ReactivateAsync(Teacher));
        }

        Assert.True(IsInactive(Teacher));
    }

    [Fact]
    public async Task StaffOfTwoChapters_InactiveInOne_StayActiveInTheOther_AndAreReactivatedThereOnly()
    {
        // FR-017 (27/10/2026): inactivity does not affect membership or access in another chapter.
        using (var seed = Caller(new FakeCurrentUserContext(null) { SeesAllChapters = true, ActiveChapterId = null }))
        {
            seed.Db.UserRoles.Add(new UserRole { UserId = Teacher, Role = Role.Teacher, ChapterId = Coventry });
            await seed.Db.SaveChangesAsync();
        }

        await MarkInactiveAsync(Teacher);

        Assert.True(IsInactive(Teacher));
        Assert.False(IsInactive(Teacher, Coventry));
        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.Equal([Coventry], ChapterMembership.ChapterIds(check.Db, Teacher));
            Assert.DoesNotContain(Teacher, ChapterMembership.StaffIn(check.Db, Chapter.ShrewsburyId, Role.Teacher).Select(u => u.Id).ToList());
            Assert.Contains(Teacher, ChapterMembership.StaffIn(check.Db, Coventry, Role.Teacher).Select(u => u.Id).ToList());
        }

        using (var coventry = Caller(new FakeCurrentUserContext(AdminTwo, Role.Admin) { ActiveChapterId = Coventry }))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coventry.Leaving.ReactivateAsync(Teacher));
        }

        await ReactivateAsync(Teacher);
        Assert.False(IsInactive(Teacher));
        using var verify = Caller(AdminOne, Role.Admin);
        Assert.Equal([Chapter.ShrewsburyId, Coventry], ChapterMembership.ChapterIds(verify.Db, Teacher));
    }

    [Fact]
    public async Task Reactivate_SomeoneWhoIsNotInactive_OrHoldsNoStaffRole_IsRefused()
    {
        using var admin = Caller(AdminOne, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReactivateAsync(Teacher));

        // A parent is not staff to reactivate.
        admin.Db.AppUsers.Add(new AppUser { Id = 60, DisplayName = "Parent Only", Email = "p60@example.com" });
        admin.Db.UserRoles.Add(new UserRole { UserId = 60, Role = Role.Parent });
        await admin.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReactivateAsync(60));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Leaving.ReactivateAsync(9999));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task WhileInactive_AStaffRoleCanBeGranted_ButGivesNothingUntilReactivation(Role role)
    {
        // FR-017 (27/10/2026): an Admin may change their roles, but no grant restores chapter access
        // while they are Inactive there.
        await MarkInactiveAsync(Safeguarding);

        using (var admin = Caller(AdminOne, Role.Admin))
        {
            if (role == Role.SafeguardingOfficer)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Roles.GrantAsync(Safeguarding, role));
            }
            else
            {
                await admin.Roles.GrantAsync(Safeguarding, role);
            }
        }

        Assert.True(IsInactive(Safeguarding));
        await ReactivateAsync(Safeguarding);
        Assert.False(IsInactive(Safeguarding));
        Assert.Equal(
            new[] { Role.SafeguardingOfficer, role }.Distinct().Order().Select(r => (r, (int?)Chapter.ShrewsburyId)).ToList(),
            RolesOf(Safeguarding));
    }

    [Fact]
    public async Task WhileInactive_ParentCanBeGranted_ButCountsOnlyOnReactivation_AndMfaStillApplies()
    {
        await SignInFor(Teacher, withAuthenticator: false);
        await MarkInactiveAsync(Teacher);

        using (var admin = Caller(AdminOne, Role.Admin))
        {
            await admin.Roles.GrantAsync(Teacher, Role.Parent);
        }

        using (var check = Caller(AdminOne, Role.Admin))
        {
            Assert.False(await InactiveStaffPolicy.IsLockedOutAsync(check.Db, Teacher));
            Assert.True(ChapterMembership.IsInactiveInChapter(check.Db, Teacher, Chapter.ShrewsburyId));
            Assert.True(InactiveStaffPolicy.HoldsCountingStaffRoleInAnyChapter(check.Db, Teacher));
        }

        await ReactivateAsync(Teacher);
        Assert.Equal(
            [(Role.Teacher, (int?)Chapter.ShrewsburyId), (Role.Parent, (int?)Chapter.ShrewsburyId)],
            RolesOf(Teacher));
    }

    [Fact]
    public async Task InactiveGuardianFromAnInvite_IsListedAsWaiting_AndCanBeApprovedAsParent()
    {
        await MarkInactiveAsync(Teacher);
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
        Assert.True(IsInactive(Teacher));
    }

    [Fact]
    public async Task History_KeepsEachPeriod_WithWhoActedAndWhen()
    {
        var start = DateTime.UtcNow;
        await MarkInactiveAsync(Teacher, by: AdminOne);
        await ReactivateAsync(Teacher, by: AdminTwo);
        await MarkInactiveAsync(Teacher, by: AdminTwo);
        await ReactivateAsync(Teacher, by: AdminOne);
        var end = DateTime.UtcNow;

        var history = HistoryOf(Teacher);
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(Chapter.ShrewsburyId, h.ChapterId));
        Assert.Equal((AdminOne, AdminTwo), (history[0].MarkedInactiveByUserId!.Value, history[0].ReactivatedByUserId!.Value));
        Assert.Equal((AdminTwo, AdminOne), (history[1].MarkedInactiveByUserId!.Value, history[1].ReactivatedByUserId!.Value));
        Assert.All(history, h =>
        {
            Assert.InRange(h.InactiveAt, start, end);
            Assert.InRange(h.ReactivatedAt!.Value, h.InactiveAt, end);
        });
        Assert.True(history[1].InactiveAt >= history[0].ReactivatedAt);

        using var admin = Caller(AdminOne, Role.Admin);
        var member = Assert.Single(await admin.Leaving.ListStaffAsync(), m => m.UserId == Teacher);
        Assert.False(member.IsInactive);
        Assert.Equal(["Admin One", "Admin Two"], member.History.Select(p => p.MarkedInactiveBy));
        Assert.Equal(["Admin Two", "Admin One"], member.History.Select(p => p.ReactivatedBy));
    }

    [Fact]
    public async Task MarkInactive_OpensAHistoryRow_ListedAsInactive()
    {
        await MarkInactiveAsync(Teacher);

        var row = Assert.Single(HistoryOf(Teacher));
        Assert.Equal(AdminOne, row.MarkedInactiveByUserId);
        Assert.Null(row.ReactivatedAt);

        using var admin = Caller(AdminOne, Role.Admin);
        var member = Assert.Single(await admin.Leaving.ListStaffAsync(), m => m.UserId == Teacher);
        Assert.True(member.IsInactive);
        Assert.Equal([Role.Teacher], member.Roles);
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

    private async Task BackdateInactiveAsync(int userId, DateTime inactiveAt)
    {
        using var seed = Caller(AdminOne, Role.Admin);
        var open = seed.Db.StaffInactivities.Single(s => s.UserId == userId && s.ReactivatedAt == null);
        seed.Db.Entry(open).CurrentValues[nameof(StaffInactivity.InactiveAt)] = inactiveAt;
        await seed.Db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DbsCheckDueForDeletion>> DbsDueAsync()
    {
        using var admin = Caller(AdminOne, Role.Admin);
        return await RetentionFlagging.GetDbsChecksDueForDeletionAsync(admin.Db, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task MarkingInactiveStartsTheClock_ReactivatingStopsIt_AndMarkingInactiveAgainRestartsItFromThen()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddDbsCheckAsync(Teacher, today.AddYears(-1));
        await MarkInactiveAsync(Teacher);
        await BackdateInactiveAsync(Teacher, DateTime.UtcNow.AddYears(-7));
        Assert.Equal(DbsCheckDeletionReason.Inactive, Assert.Single(await DbsDueAsync()).Reason);

        await ReactivateAsync(Teacher);
        Assert.Empty(await DbsDueAsync());

        await MarkInactiveAsync(Teacher);
        Assert.Empty(await DbsDueAsync());   // the clock runs from the current Inactive record, today
        Assert.InRange(HistoryOf(Teacher).Single(h => h.ReactivatedAt == null).InactiveAt, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
    }

    [Fact]
    public async Task ReactivatedTeacherWithAnExpiredDbsCheck_IsReactivated_AndFlaggedAsExpired()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddDbsCheckAsync(Teacher, today.AddDays(-10));
        await MarkInactiveAsync(Teacher);

        await ReactivateAsync(Teacher);

        Assert.False(IsInactive(Teacher));
        using var job = Caller(Vlms.Infrastructure.Security.SystemCurrentUserContext.Instance);
        var result = await new Vlms.Infrastructure.Safeguarding.ConsentExpiryJob(
            job.Db, Vlms.Infrastructure.Security.SystemCurrentUserContext.Instance,
            new ListLogger<Vlms.Infrastructure.Safeguarding.ConsentExpiryJob>()).RunAsync();
        var flag = Assert.Single(result.DbsFlags, f => f.TeacherUserId == Teacher);
        Assert.True(flag.IsExpired);
        Assert.Equal(70, flag.DbsCheckId);
    }
}
