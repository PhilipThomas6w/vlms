using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Registration;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// "Link to existing guardian" at final approval (adr/0008 decision 8, 27/10/2026): candidates are
/// guardian records in the application's chapter with no account whose email matches the
/// applicant's (trimmed, case-insensitive); choosing one attaches the applicant's account to it
/// instead of creating a second guardian record, Admin only, re-checked at submit time.
/// </summary>
public sealed class StudentApplicationGuardianLinkTests : IDisposable
{
    private const int AdminId = 1;
    private const int SafeguardingId = 2;
    private const int TeacherId = 10;
    private const int ApplicantId = 30;
    private const int OtherUserId = 31;
    private const int OwnGuardianApplicantId = 34;
    private const int OtherChapterId = 2;
    private const int StaffStudentId = 100;

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly FakeNotificationService _notifications = new();

    public StudentApplicationGuardianLinkTests()
    {
        _connectionString = $"Data Source=file:vlms-guardian-link-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = SeedContext();
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = OtherChapterId, Name = "Other", Place = "Elsewhere", CreatedAt = DateTime.UtcNow, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        void User(int id, string name, string email, params Role[] roles)
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = email });
            foreach (var role in roles)
            {
                db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = Chapter.ShrewsburyId });
            }
        }

        User(AdminId, "Admin One", "admin@example.com", Role.Admin);
        User(SafeguardingId, "Safeguarding One", "so@example.com", Role.SafeguardingOfficer);
        User(TeacherId, "Teacher One", "teacher@example.com", Role.Teacher);
        User(ApplicantId, "Jane Applicant", "Jane.Parent@Example.com");
        User(OtherUserId, "Other User", "other@example.com");
        User(OwnGuardianApplicantId, "Erin Parent", "erin@example.com", Role.Parent);

        db.Students.Add(new Student
        {
            Id = StaffStudentId, Name = "Kim Child", DateOfBirth = new DateOnly(2015, 3, 4), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1), ChapterId = Chapter.ShrewsburyId
        });
        db.ParentGuardians.Add(new ParentGuardian
        {
            Id = 1, Name = "Erin Parent", ContactInfo = "erin@example.com", IsPrimary = true, AppUserId = OwnGuardianApplicantId,
            ChapterId = Chapter.ShrewsburyId
        });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext SeedContext() =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);

    private sealed class CallerScope(ServiceProvider provider, IServiceScope scope, INotificationService notifications) : IDisposable
    {
        public VlmsDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        public StudentApplicationService Service => new(
            Db,
            scope.ServiceProvider.GetRequiredService<ICurrentUserContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>(),
            new RegistrationNotificationService(Db, notifications, new ListLogger<RegistrationNotificationService>(), "https://www.example.test"));

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerScope Caller(int userId, params Role[] roles)
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, new FakeCurrentUserContext(userId, true, roles));
        return new CallerScope(provider, provider.CreateScope(), _notifications);
    }

    private void AddGuardian(
        int id, string contactInfo, int chapterId = Chapter.ShrewsburyId, int? appUserId = null, string? phone = null,
        string? address = null, int? linkedStudentId = null)
    {
        using var db = SeedContext();
        db.ParentGuardians.Add(new ParentGuardian
        {
            Id = id, Name = $"Guardian {id}", ContactInfo = contactInfo, IsPrimary = true, AppUserId = appUserId,
            Phone = phone, PostalAddress = address, ChapterId = chapterId
        });
        if (linkedStudentId is int studentId)
        {
            db.StudentGuardianLinks.Add(new StudentGuardianLink
            {
                StudentId = studentId, ParentGuardianId = id, CreatedByUserId = AdminId, Relationship = "Mother", ChapterId = chapterId
            });
        }

        db.SaveChanges();
    }

    private async Task<int> ApplicationAtStageTwoAsync(int applicantId = ApplicantId, string childName = "Chris Child", DateOnly? dateOfBirth = null)
    {
        int id;
        using (var applicant = Caller(applicantId))
        {
            var child = new ChildApplicationForm(
                childName, dateOfBirth ?? new DateOnly(2016, 5, 6), "Mother", true, true, false, true, "Grandma 07000 111222", null, null, null);
            id = (await applicant.Service.SubmitAsync(Chapter.ShrewsburyId, new ApplicantDetails("07000 000001", "1 Church Lane"), [child])).Single().Id;
        }

        using var so = Caller(SafeguardingId, Role.SafeguardingOfficer);
        await so.Service.SafeguardingApproveAsync(id);
        return id;
    }

    private ParentGuardian Guardian(int id)
    {
        using var db = SeedContext();
        return db.ParentGuardians.AsNoTracking().Single(g => g.Id == id);
    }

    private int GuardianCount()
    {
        using var db = SeedContext();
        return db.ParentGuardians.Count();
    }

    // ------------------------------------------------------------------ candidates

    [Fact]
    public async Task Candidates_AreSameChapter_NoAccount_EmailTrimmedAndCaseInsensitive_InIdOrder()
    {
        AddGuardian(50, "  jane.parent@EXAMPLE.com ", linkedStudentId: StaffStudentId);
        AddGuardian(51, "jane.parent@example.com", chapterId: OtherChapterId);
        AddGuardian(52, "jane.parent@example.com", appUserId: OtherUserId);
        AddGuardian(53, "someone.else@example.com");
        AddGuardian(54, "Jane.Parent@Example.com");
        var id = await ApplicationAtStageTwoAsync();

        using var admin = Caller(AdminId, Role.Admin);
        var page = await admin.Service.GetForApprovalAsync(id);

        Assert.Equal([50, 54], page.GuardiansWithSameEmail.Select(g => g.ParentGuardianId));
        Assert.Equal("Guardian 50", page.GuardiansWithSameEmail[0].Name);
        Assert.Equal(["Kim Child"], page.GuardiansWithSameEmail[0].ChildNames);
        Assert.Empty(page.GuardiansWithSameEmail[1].ChildNames);
    }

    [Fact]
    public async Task Candidates_None_WhenTheApplicantAlreadyHasTheirOwnGuardianRecordInTheChapter()
    {
        AddGuardian(50, "erin@example.com");
        var id = await ApplicationAtStageTwoAsync(OwnGuardianApplicantId);

        using var admin = Caller(AdminId, Role.Admin);
        Assert.Empty((await admin.Service.GetForApprovalAsync(id)).GuardiansWithSameEmail);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50));
        Assert.Null(Guardian(50).AppUserId);
    }

    // ------------------------------------------------------------------ link path

    [Fact]
    public async Task LinkToExistingGuardian_AttachesTheAccount_KeepsTheRecordsFields_FillsOnlyEmptyOnes()
    {
        AddGuardian(50, "jane.parent@example.com", phone: "01743 000000", address: null, linkedStudentId: StaffStudentId);
        var id = await ApplicationAtStageTwoAsync();
        var guardiansBefore = GuardianCount();

        int studentId;
        using (var admin = Caller(AdminId, Role.Admin))
        {
            studentId = await admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50);
        }

        Assert.Equal(guardiansBefore, GuardianCount());
        var guardian = Guardian(50);
        Assert.Equal(ApplicantId, guardian.AppUserId);
        Assert.Equal("Guardian 50", guardian.Name);
        Assert.Equal("jane.parent@example.com", guardian.ContactInfo);
        Assert.Equal("01743 000000", guardian.Phone);
        Assert.Equal("1 Church Lane", guardian.PostalAddress);

        using var db = SeedContext();
        var link = db.StudentGuardianLinks.Single(l => l.StudentId == studentId);
        Assert.Equal(50, link.ParentGuardianId);
        Assert.Equal(AdminId, link.CreatedByUserId);
        Assert.Equal("Mother", link.Relationship);
        Assert.Equal(50, db.ConsentRecords.Single(c => c.StudentId == studentId).SubmittedByParentId);
        Assert.Single(db.StudentGuardianLinks.Where(l => l.StudentId == StaffStudentId && l.ParentGuardianId == 50));
        var application = db.StudentApplications.Single(a => a.Id == id);
        Assert.Equal(StudentApplicationStatus.Approved, application.Status);
        Assert.Equal(AdminId, application.AdminDecidedByUserId);
        Assert.Contains(db.UserRoles, r => r.UserId == ApplicantId && r.Role == Role.Parent && r.ChapterId == Chapter.ShrewsburyId);

        // The parent's account now reaches the staff-registered child too.
        using var parent = Caller(ApplicantId, Role.Parent);
        Assert.Equal([StaffStudentId, studentId], parent.Db.StudentGuardianLinks
            .Where(l => l.ParentGuardian!.AppUserId == ApplicantId).Select(l => l.StudentId).OrderBy(s => s).ToList());
    }

    [Fact]
    public async Task LinkToExistingGuardian_AlreadyLinkedToTheExistingStudent_KeepsThatLink()
    {
        AddGuardian(50, "jane.parent@example.com", linkedStudentId: StaffStudentId);
        var id = await ApplicationAtStageTwoAsync(childName: "Kim Child", dateOfBirth: new DateOnly(2015, 3, 4));

        using (var admin = Caller(AdminId, Role.Admin))
        {
            Assert.Equal(StaffStudentId, await admin.Service.ApproveAsync(id, startingRankId: null, existingStudentId: StaffStudentId, existingGuardianId: 50));
        }

        using var db = SeedContext();
        Assert.Single(db.StudentGuardianLinks.Where(l => l.StudentId == StaffStudentId));
        Assert.Equal(ApplicantId, Guardian(50).AppUserId);
        Assert.Single(db.ConsentRecords.Where(c => c.StudentId == StaffStudentId));
    }

    [Fact]
    public async Task LinkToExistingGuardian_WithAChildOfTheSameNameAlready_RefusesANewStudent()
    {
        AddGuardian(50, "jane.parent@example.com", linkedStudentId: StaffStudentId);
        var id = await ApplicationAtStageTwoAsync(childName: "kim  child");

        using var admin = Caller(AdminId, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50));
        Assert.Null(Guardian(50).AppUserId);
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, admin.Db.StudentApplications.AsNoTracking().Single(a => a.Id == id).Status);
    }

    // ------------------------------------------------------------------ normal path

    [Fact]
    public async Task NormalApproval_WithACandidatePresent_CreatesTheApplicantsOwnRecord_AsBefore()
    {
        AddGuardian(50, "jane.parent@example.com");
        var id = await ApplicationAtStageTwoAsync();
        var guardiansBefore = GuardianCount();

        using (var admin = Caller(AdminId, Role.Admin))
        {
            await admin.Service.ApproveAsync(id, startingRankId: 1);
        }

        Assert.Equal(guardiansBefore + 1, GuardianCount());
        Assert.Null(Guardian(50).AppUserId);
        using var db = SeedContext();
        var own = db.ParentGuardians.Single(g => g.AppUserId == ApplicantId);
        Assert.Equal("Jane Applicant", own.Name);
        Assert.Equal("07000 000001", own.Phone);
    }

    // ------------------------------------------------------------------ refusals

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Parent)]
    public async Task NonAdmin_IsRefused(Role role)
    {
        AddGuardian(50, "jane.parent@example.com");
        var id = await ApplicationAtStageTwoAsync();

        using var caller = Caller(role == Role.SafeguardingOfficer ? SafeguardingId : TeacherId, role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            caller.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50));
        Assert.Null(Guardian(50).AppUserId);
    }

    [Fact]
    public async Task AdminWithoutMfa_IsRefused()
    {
        AddGuardian(50, "jane.parent@example.com");
        var id = await ApplicationAtStageTwoAsync();

        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, new FakeCurrentUserContext(AdminId, false, Role.Admin));
        using var caller = new CallerScope(provider, provider.CreateScope(), _notifications);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            caller.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50));
        Assert.Null(Guardian(50).AppUserId);
    }

    [Fact]
    public async Task StaleChoice_TheRecordGainedAnAccount_IsRefused_AndNothingIsCreated()
    {
        AddGuardian(50, "jane.parent@example.com");
        var id = await ApplicationAtStageTwoAsync();

        using var admin = Caller(AdminId, Role.Admin);
        Assert.Single((await admin.Service.GetForApprovalAsync(id)).GuardiansWithSameEmail);

        using (var db = SeedContext())
        {
            db.ParentGuardians.Where(g => g.Id == 50).ExecuteUpdate(s => s.SetProperty(g => g.AppUserId, OtherUserId));
        }

        using var submit = Caller(AdminId, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            submit.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 50));
        Assert.Equal(OtherUserId, Guardian(50).AppUserId);
        Assert.Empty(submit.Db.Students.Where(s => s.Name == "Chris Child"));
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, submit.Db.StudentApplications.AsNoTracking().Single(a => a.Id == id).Status);
    }

    [Fact]
    public async Task OtherChapterRecord_OrDifferentEmail_IsRefused()
    {
        AddGuardian(51, "jane.parent@example.com", chapterId: OtherChapterId);
        AddGuardian(53, "someone.else@example.com");
        var id = await ApplicationAtStageTwoAsync();

        using var admin = Caller(AdminId, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 51));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: null, existingGuardianId: 53));
        Assert.Null(Guardian(51).AppUserId);
        Assert.Null(Guardian(53).AppUserId);
    }
}
