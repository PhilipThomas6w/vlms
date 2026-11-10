using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Registration;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="RegistrationQueueService"/> (adr/0017, FR-021 as amended 10/11/2026): one
/// queue per chapter of everyone waiting for a role (self-registrations whatever they are joining
/// as, and guardian-invite acceptors not yet a Parent), excluding role holders; several roles granted
/// in one go, all or none, taking the person off the queue; dismissal; the menu and home-page count;
/// a teacher-to-be's DBS status; and a role-less caller's own status for the home page.
/// </summary>
public sealed class RegistrationQueueServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int ShrewsburyAdminId = 9001;
    private const int CoventryAdminId = 9002;
    private const int TeacherId = 9003;

    private const int JoiningAsParent = 101;
    private const int JoiningAsTeacher = 102;
    private const int JoiningAsOther = 103;
    private const int RegisteredButAlreadyATeacher = 104;
    private const int Dismissed = 105;
    private const int RegisteredInCoventry = 106;
    private const int TeacherWithExpiredDbs = 107;
    private const int InviteAcceptor = 110;
    private const int InviteAcceptorWhoIsATeacher = 111;
    private const int InviteAcceptorAlreadyAParent = 112;

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public RegistrationQueueServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-registration-queue-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });

        void User(int id, string name, int? chapterId = null, params Role[] roles)
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
            foreach (var role in roles)
            {
                db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapterId });
            }
        }

        var registeredAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        void Request(int userId, int chapterId, JoiningAs joiningAs, int dayOffset, bool dismissed = false) =>
            db.ChapterJoinRequests.Add(new ChapterJoinRequest
            {
                UserId = userId, ChapterId = chapterId, JoiningAs = joiningAs, RequestedAt = registeredAt.AddDays(dayOffset),
                DismissedAt = dismissed ? registeredAt.AddDays(dayOffset + 1) : null, DismissedByUserId = dismissed ? ShrewsburyAdminId : null
            });

        User(ShrewsburyAdminId, "Shrewsbury Admin", Chapter.ShrewsburyId, Role.Admin);
        User(CoventryAdminId, "Coventry Admin", Coventry, Role.Admin);
        User(TeacherId, "Teacher One", Chapter.ShrewsburyId, Role.Teacher);

        User(JoiningAsParent, "Paula Parent");
        Request(JoiningAsParent, Chapter.ShrewsburyId, JoiningAs.Parent, 0);
        User(JoiningAsTeacher, "Tom Teacher");
        Request(JoiningAsTeacher, Chapter.ShrewsburyId, JoiningAs.Teacher, 1);
        User(JoiningAsOther, "Olive Other");
        Request(JoiningAsOther, Chapter.ShrewsburyId, JoiningAs.Other, 2);
        User(RegisteredButAlreadyATeacher, "Already Teacher", Chapter.ShrewsburyId, Role.Teacher);
        Request(RegisteredButAlreadyATeacher, Chapter.ShrewsburyId, JoiningAs.Teacher, 3);
        User(Dismissed, "Dee Dismissed");
        Request(Dismissed, Chapter.ShrewsburyId, JoiningAs.Other, 4, dismissed: true);
        User(RegisteredInCoventry, "Cora Coventry");
        Request(RegisteredInCoventry, Coventry, JoiningAs.Parent, 5);
        User(TeacherWithExpiredDbs, "Eddie Expired");
        Request(TeacherWithExpiredDbs, Chapter.ShrewsburyId, JoiningAs.Teacher, 6);

        db.DbsChecks.Add(new DbsCheck
        {
            Id = 1, ChapterId = Chapter.ShrewsburyId, TeacherUserId = JoiningAsTeacher, CheckDate = new DateOnly(2025, 1, 1),
            ExpiryDate = new DateOnly(2099, 1, 31), CertificateNumber = "DBS1", Status = DbsCheckStatus.Clear
        });
        db.DbsChecks.Add(new DbsCheck
        {
            Id = 2, ChapterId = Chapter.ShrewsburyId, TeacherUserId = TeacherWithExpiredDbs, CheckDate = new DateOnly(2020, 1, 1),
            ExpiryDate = new DateOnly(2023, 1, 1), CertificateNumber = "DBS2", Status = DbsCheckStatus.Clear
        });

        User(InviteAcceptor, "Ian Invited");
        User(InviteAcceptorWhoIsATeacher, "Tess Teacher-Parent", Chapter.ShrewsburyId, Role.Teacher);
        User(InviteAcceptorAlreadyAParent, "Pat Parent", Chapter.ShrewsburyId, Role.Parent);
        db.Students.Add(new Student
        {
            Id = 1, ChapterId = Chapter.ShrewsburyId, Name = "Kim Child", DateOfBirth = new DateOnly(2015, 3, 4), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        var guardianId = 1;
        foreach (var userId in new[] { InviteAcceptor, InviteAcceptorWhoIsATeacher, InviteAcceptorAlreadyAParent })
        {
            db.ParentGuardians.Add(new ParentGuardian
            {
                Id = guardianId, ChapterId = Chapter.ShrewsburyId, Name = $"Guardian {userId}", ContactInfo = $"user{userId}@example.com",
                IsPrimary = true, AppUserId = userId
            });
            db.StudentGuardianLinks.Add(new StudentGuardianLink
            {
                StudentId = 1, ParentGuardianId = guardianId, CreatedByUserId = ShrewsburyAdminId, ChapterId = Chapter.ShrewsburyId
            });
            db.GuardianInvites.Add(new GuardianInvite
            {
                Id = guardianId, ChapterId = Chapter.ShrewsburyId, StudentId = 1, ParentGuardianId = guardianId, CreatedByUserId = ShrewsburyAdminId,
                CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ExpiresAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
                ConsumedAt = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), ConsumedByAppUserId = userId
            });
            guardianId++;
        }

        db.StudentApplications.Add(new StudentApplication
        {
            Id = 1, ChapterId = Chapter.ShrewsburyId, ApplicantUserId = InviteAcceptorAlreadyAParent, ParentPhone = "07000 000000",
            ParentPostalAddress = "1 Church Lane", Relationship = "Mother", ChildName = "Awaiting Admin", ChildDateOfBirth = new DateOnly(2016, 1, 1),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = StudentApplicationStatus.AwaitingAdminApproval, SubmittedAt = registeredAt, LastSubmittedAt = registeredAt,
            SafeguardingReviewedAt = registeredAt
        });
        db.StudentApplications.Add(new StudentApplication
        {
            Id = 2, ChapterId = Chapter.ShrewsburyId, ApplicantUserId = InviteAcceptorAlreadyAParent, ParentPhone = "07000 000000",
            ParentPostalAddress = "1 Church Lane", Relationship = "Mother", ChildName = "With Safeguarding", ChildDateOfBirth = new DateOnly(2016, 1, 1),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = StudentApplicationStatus.AwaitingSafeguardingReview, SubmittedAt = registeredAt, LastSubmittedAt = registeredAt
        });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private sealed class CallerScope(ServiceProvider provider, IServiceScope scope) : IDisposable
    {
        public VlmsDbContext Db => scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        public RegistrationQueueService Queue => scope.ServiceProvider.GetRequiredService<RegistrationQueueService>();

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerScope Caller(ICurrentUserContext user)
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, user, configure: services =>
        {
            services.AddScoped<RoleAssignmentService>();
            services.AddScoped(sp => new NewRegistrationService(
                sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(),
                sp.GetRequiredService<AppUserProvisioningService>(), sp.GetRequiredService<INotificationService>(),
                NullLogger<NewRegistrationService>.Instance, "https://test.example"));
            services.AddScoped<RegistrationQueueService>();
        });
        return new CallerScope(provider, provider.CreateScope());
    }

    private CallerScope ShrewsburyAdmin(bool mfa = true) =>
        Caller(new FakeCurrentUserContext(ShrewsburyAdminId, mfa, Role.Admin) { ActiveChapterId = Chapter.ShrewsburyId });

    private CallerScope CoventryAdmin() => Caller(new FakeCurrentUserContext(CoventryAdminId, Role.Admin) { ActiveChapterId = Coventry });

    private CallerScope RoleLess(int userId) => Caller(new FakeCurrentUserContext(userId) { ActiveChapterId = null });

    private List<Role> RolesIn(int userId, int chapterId)
    {
        using var admin = ShrewsburyAdmin();
        return admin.Db.UserRoles.Where(r => r.UserId == userId && r.ChapterId == chapterId).Select(r => r.Role).OrderBy(r => r).ToList();
    }

    [Fact]
    public async Task Queue_HoldsEveryoneWaiting_WhateverTheyJoinAs_AndInviteAcceptors_ButNoRoleHolders()
    {
        using var admin = ShrewsburyAdmin();
        var queue = await admin.Queue.ListAsync();

        Assert.Equal(
            new[] { JoiningAsParent, JoiningAsTeacher, JoiningAsOther, TeacherWithExpiredDbs, InviteAcceptor, InviteAcceptorWhoIsATeacher }
                .Order(),
            queue.Select(e => e.UserId).Order());

        var parent = queue.Single(e => e.UserId == JoiningAsParent);
        Assert.Equal(JoiningAs.Parent, parent.JoiningAs);
        Assert.Equal([QueueSource.SelfRegistration], parent.Sources);
        Assert.True(parent.CanDismiss);
        Assert.Null(parent.Dbs);

        var invited = queue.Single(e => e.UserId == InviteAcceptor);
        Assert.Equal([QueueSource.GuardianInvite], invited.Sources);
        Assert.Equal(["Kim Child"], invited.LinkedStudentNames);
        Assert.False(invited.CanDismiss);
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), invited.RegisteredAt);

        Assert.Equal([Role.Teacher], queue.Single(e => e.UserId == InviteAcceptorWhoIsATeacher).CurrentRoles);

        // Another chapter's queue is its own.
        using var coventry = CoventryAdmin();
        Assert.Equal([RegisteredInCoventry], (await coventry.Queue.ListAsync()).Select(e => e.UserId));
    }

    [Fact]
    public async Task Queue_ShowsATeacherToBesDbsStatus_ForTeachersOnly()
    {
        using var admin = ShrewsburyAdmin();
        var queue = await admin.Queue.ListAsync();

        Assert.Equal(new QueueDbsStatus(QueueDbsState.Valid, new DateOnly(2099, 1, 31)), queue.Single(e => e.UserId == JoiningAsTeacher).Dbs);
        Assert.Equal(new QueueDbsStatus(QueueDbsState.Expired, new DateOnly(2023, 1, 1)), queue.Single(e => e.UserId == TeacherWithExpiredDbs).Dbs);
        Assert.Null(queue.Single(e => e.UserId == JoiningAsOther).Dbs);

        using (var seed = ShrewsburyAdmin())
        {
            seed.Db.DbsChecks.Remove(seed.Db.DbsChecks.Single(d => d.Id == 1));
            seed.Db.SaveChanges();
        }

        using var again = ShrewsburyAdmin();
        Assert.Equal(new QueueDbsStatus(QueueDbsState.None, null), (await again.Queue.ListAsync()).Single(e => e.UserId == JoiningAsTeacher).Dbs);
    }

    [Fact]
    public async Task GrantingSeveralRoles_GrantsThemAllInOneGo_AndTakesThePersonOffTheQueue()
    {
        using (var admin = ShrewsburyAdmin())
        {
            await admin.Queue.GrantAsync(JoiningAsTeacher, [Role.Teacher, Role.Parent]);
        }

        Assert.Equal([Role.Teacher, Role.Parent], RolesIn(JoiningAsTeacher, Chapter.ShrewsburyId));
        using var check = ShrewsburyAdmin();
        Assert.DoesNotContain(await check.Queue.ListAsync(), e => e.UserId == JoiningAsTeacher);
    }

    [Fact]
    public async Task GrantingParent_ToAnInviteAcceptor_TakesThemOffTheQueue_AndKeepsTheirOtherRoles()
    {
        using (var admin = ShrewsburyAdmin())
        {
            await admin.Queue.GrantAsync(InviteAcceptorWhoIsATeacher, [Role.Parent]);
        }

        Assert.Equal([Role.Teacher, Role.Parent], RolesIn(InviteAcceptorWhoIsATeacher, Chapter.ShrewsburyId));
        using var check = ShrewsburyAdmin();
        Assert.DoesNotContain(await check.Queue.ListAsync(), e => e.UserId == InviteAcceptorWhoIsATeacher);
    }

    [Theory]
    [InlineData(Role.Approver)]
    [InlineData(Role.Student)]
    public async Task Grant_IsAllOrNone_AnUngrantableRoleRefusesTheLot(Role ungrantable)
    {
        using (var admin = ShrewsburyAdmin())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Queue.GrantAsync(JoiningAsParent, [Role.Parent, ungrantable]));
        }

        Assert.Empty(RolesIn(JoiningAsParent, Chapter.ShrewsburyId));
        using var check = ShrewsburyAdmin();
        Assert.Contains(await check.Queue.ListAsync(), e => e.UserId == JoiningAsParent);
    }

    [Theory]
    [InlineData(RegisteredInCoventry)]
    [InlineData(RegisteredButAlreadyATeacher)]
    [InlineData(Dismissed)]
    public async Task Grant_ToSomeoneNotOnThisChaptersQueue_IsRefused(int userId)
    {
        var before = RolesIn(userId, Chapter.ShrewsburyId);
        using (var admin = ShrewsburyAdmin())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Queue.GrantAsync(userId, [Role.Parent]));
        }

        Assert.Equal(before, RolesIn(userId, Chapter.ShrewsburyId));
    }

    [Fact]
    public async Task Queue_ByAnyoneButAnAdmin_IsRefused()
    {
        using var teacher = Caller(new FakeCurrentUserContext(TeacherId, Role.Teacher));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Queue.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Queue.GrantAsync(JoiningAsParent, [Role.Parent]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Queue.DismissAsync(JoiningAsParent));
        Assert.Empty(RolesIn(JoiningAsParent, Chapter.ShrewsburyId));
    }

    [Fact]
    public async Task Dismiss_TakesThemOffWithoutARole_DeletesNothing_AndTheyAreToldItWasNotApproved()
    {
        using (var admin = ShrewsburyAdmin())
        {
            await admin.Queue.DismissAsync(JoiningAsOther);
            Assert.DoesNotContain(await admin.Queue.ListAsync(), e => e.UserId == JoiningAsOther);
            Assert.True(await admin.Db.AppUsers.AnyAsync(u => u.Id == JoiningAsOther));
        }

        using var person = RoleLess(JoiningAsOther);
        var status = await person.Queue.GetMyStatusAsync();
        Assert.False(status.HoldsARole);
        Assert.True(status.Dismissed);
        Assert.Empty(status.WaitingInChapters);
    }

    [Fact]
    public async Task Count_IsPeopleWaitingPlusApplicationsAwaitingTheAdmin_ForAnAdminWithMfaOnly()
    {
        using (var admin = ShrewsburyAdmin())
        {
            // Six people waiting and one application awaiting final approval; the one still with the
            // Safeguarding Officer is not counted.
            Assert.Equal(7, await admin.Queue.CountWaitingAsync());
            await admin.Queue.GrantAsync(JoiningAsParent, [Role.Parent]);
        }

        using (var admin = ShrewsburyAdmin())
        {
            Assert.Equal(6, await admin.Queue.CountWaitingAsync());
            Assert.Equal(5, (await admin.Queue.ListAsync()).Count);
        }

        using (var coventry = CoventryAdmin()) Assert.Equal(1, await coventry.Queue.CountWaitingAsync());
        using (var noMfa = ShrewsburyAdmin(mfa: false)) Assert.Equal(0, await noMfa.Queue.CountWaitingAsync());
        using (var teacher = Caller(new FakeCurrentUserContext(TeacherId, Role.Teacher))) Assert.Equal(0, await teacher.Queue.CountWaitingAsync());
        using (var nobody = RoleLess(JoiningAsTeacher)) Assert.Equal(0, await nobody.Queue.CountWaitingAsync());
    }

    [Fact]
    public async Task MyStatus_SaysWhichChapterWillGiveAccess_ForSelfRegistrationsAndInviteAcceptors()
    {
        using (var registered = RoleLess(JoiningAsParent))
        {
            Assert.Equal(["Shrewsbury Orthodox Church"], (await registered.Queue.GetMyStatusAsync()).WaitingInChapters);
            Assert.False((await registered.Queue.GetMyStatusAsync()).HoldsARole);
        }

        using (var invited = RoleLess(InviteAcceptor))
        {
            var status = await invited.Queue.GetMyStatusAsync();
            Assert.Equal(["Shrewsbury Orthodox Church"], status.WaitingInChapters);
            Assert.False(status.Dismissed);
        }

        using (var dismissed = RoleLess(Dismissed))
        {
            var status = await dismissed.Queue.GetMyStatusAsync();
            Assert.True(status.Dismissed);
            Assert.Empty(status.WaitingInChapters);
        }

        using var teacher = Caller(new FakeCurrentUserContext(TeacherId, Role.Teacher));
        Assert.True((await teacher.Queue.GetMyStatusAsync()).HoldsARole);
    }
}
