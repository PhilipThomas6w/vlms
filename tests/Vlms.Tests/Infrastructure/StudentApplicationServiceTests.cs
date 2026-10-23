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
/// Parent registration with two-stage approval (functional.md FR-004 as amended 22/10/2026,
/// adr/0008 answers 1, 4, 5, 6, 7; TC-037): nothing but the application exists before final
/// approval; stage 1 is the Safeguarding Officer's (approve, ask for more information, reject) and
/// stage 2 the Admin's, each with MFA; approval creates everything atomically; duplicates under one
/// parent are refused; a second parent can be linked to an existing student; the sensitive details
/// are restricted and every read is audited; emails carry nothing sensitive.
/// </summary>
public sealed class StudentApplicationServiceTests : IDisposable
{
    private const int AdminId = 1;
    private const int SafeguardingId = 2;
    private const int BothRolesId = 3;
    private const int TeacherId = 10;
    private const int ApproverId = 20;
    private const int ApplicantId = 30;
    private const int OtherApplicantId = 31;
    private const int LeaverId = 32;
    private const int StudentSignInId = 33;
    private const int ExistingParentId = 34;
    private const int ExistingStudentId = 100;

    private const string Medical = "Severe asthma (sensitive)";
    private const string Dietary = "Peanut allergy (sensitive)";
    private const string Sen = "Dyslexia, needs extra time (sensitive)";
    private const string Emergency = "Grandma 07000 111222 (sensitive)";
    private const string Request = "Please tell us more about the reading support (sensitive)";
    private const string Answer = "A reading ruler and extra time help (sensitive)";

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly FakeNotificationService _notifications = new();

    public StudentApplicationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-student-application-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = Caller(AdminId, Role.Admin);
        schema.Db.Database.EnsureCreated();
        Seed(schema.Db);
    }

    public void Dispose() => _anchorConnection.Dispose();

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

    private CallerScope Caller(int? userId, params Role[] roles) => Caller(userId, true, roles);

    private CallerScope Caller(int? userId, bool mfa, params Role[] roles) =>
        Caller(new FakeCurrentUserContext(userId, mfa, roles));

    private CallerScope Caller(ICurrentUserContext user, INotificationService? notifications = null)
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, user);
        return new CallerScope(provider, provider.CreateScope(), notifications ?? _notifications);
    }

    private static void Seed(VlmsDbContext db)
    {
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Reader" });
        void User(int id, string name, bool left = false, params Role[] roles)
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com", HasLeft = left, LeftAt = left ? DateTime.UtcNow.AddDays(-1) : null });
            foreach (var role in roles)
            {
                db.UserRoles.Add(new UserRole { UserId = id, Role = role });
            }
        }

        User(AdminId, "Admin One", false, Role.Admin);
        User(SafeguardingId, "Safeguarding One", false, Role.SafeguardingOfficer);
        User(BothRolesId, "Both Roles", false, Role.Admin, Role.SafeguardingOfficer);
        User(TeacherId, "Teacher One", false, Role.Teacher);
        User(ApproverId, "Approver One", false, Role.Approver);
        User(ApplicantId, "Jane Applicant");
        User(OtherApplicantId, "Pat Applicant");
        User(LeaverId, "Lee Leaver", true);
        User(StudentSignInId, "Sam Signin", false, Role.Student);
        User(ExistingParentId, "Erin Parent", false, Role.Parent);
        User(4, "Admin Left", true, Role.Admin);

        db.Students.Add(new Student
        {
            Id = ExistingStudentId, Name = "Alex Smith", DateOfBirth = new DateOnly(2015, 3, 4), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        db.Students.Add(new Student
        {
            Id = 101, Name = "Sam Signin", DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1), AppUserId = StudentSignInId
        });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 1, StudentId = ExistingStudentId, RankId = 1, StartedAt = DateTime.UtcNow.AddDays(-30) });
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Erin Parent", ContactInfo = "user34@example.com", IsPrimary = true, AppUserId = ExistingParentId });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = ExistingStudentId, ParentGuardianId = 1, CreatedByUserId = AdminId });
        db.SaveChanges();
    }

    private static ApplicantDetails Parent(string phone = "07000 000001", string address = "1 Church Lane\nShrewsbury") => new(phone, address);

    private static ChildApplicationForm Child(
        string name = "Chris Child", DateOnly? dateOfBirth = null, string relationship = "Mother", string emergency = Emergency,
        string? sen = Sen) =>
        new(name, dateOfBirth ?? new DateOnly(2016, 5, 6), relationship, true, true, false, true, emergency, Medical, Dietary, sen);

    private async Task<int> SubmitAsync(int applicantId = ApplicantId, ChildApplicationForm? child = null, params Role[] roles)
    {
        using var applicant = Caller(applicantId, roles);
        return (await applicant.Service.SubmitAsync(Parent(), [child ?? Child()])).Single().Id;
    }

    private async Task<int> SubmitAndPassStageOneAsync(int applicantId = ApplicantId, ChildApplicationForm? child = null)
    {
        var id = await SubmitAsync(applicantId, child);
        using var so = Caller(SafeguardingId, Role.SafeguardingOfficer);
        await so.Service.SafeguardingApproveAsync(id);
        return id;
    }

    private StudentApplication Load(int id)
    {
        using var admin = Caller(AdminId, Role.Admin);
        return admin.Db.StudentApplications.AsNoTracking().Single(a => a.Id == id);
    }

    private async Task<StudentApplicationSensitiveDetails> LoadDetailsAsync(int id)
    {
        using var admin = Caller(AdminId, Role.Admin);
        return await admin.Db.StudentApplicationSensitiveDetails.AsNoTracking().SingleAsync(d => d.StudentApplicationId == id);
    }

    private (int Students, int Links, int Guardians, int Consents, int ConsentDetails, int Progress) Counts()
    {
        using var admin = Caller(AdminId, Role.Admin);
        var db = admin.Db;
        return (db.Students.Count(), db.StudentGuardianLinks.Count(), db.ParentGuardians.Count(), db.ConsentRecords.Count(),
            db.ConsentSensitiveDetails.Count(), db.StudentRankProgresses.Count());
    }

    private List<Role> RolesOf(int userId)
    {
        using var admin = Caller(AdminId, Role.Admin);
        return admin.Db.UserRoles.Where(r => r.UserId == userId).Select(r => r.Role).OrderBy(r => r).ToList();
    }

    // ------------------------------------------------------------------ submission

    [Fact]
    public async Task Submit_TwoChildren_CreatesTwoApplications_AndNothingElse()
    {
        var before = Counts();

        using (var applicant = Caller(ApplicantId))
        {
            var created = await applicant.Service.SubmitAsync(Parent(), [Child("Chris Child"), Child("Dana Child", new DateOnly(2018, 1, 2), "Mother")]);
            Assert.Equal(2, created.Count);
        }

        using var admin = Caller(AdminId, Role.Admin);
        var applications = admin.Db.StudentApplications.OrderBy(a => a.Id).ToList();
        Assert.Equal(["Chris Child", "Dana Child"], applications.Select(a => a.ChildName));
        Assert.All(applications, a =>
        {
            Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview, a.Status);
            Assert.Equal(ApplicantId, a.ApplicantUserId);
            Assert.Equal("07000 000001", a.ParentPhone);
            Assert.Equal("1 Church Lane\nShrewsbury", a.ParentPostalAddress);
            Assert.Equal("Mother", a.Relationship);
            Assert.True(a.TransportConsent);
            Assert.False(a.OffsiteTripConsent);
            Assert.Null(a.StudentId);
        });
        Assert.Equal(2, admin.Db.StudentApplicationSensitiveDetails.Count());
        Assert.Equal(before, Counts());
        Assert.Empty(RolesOf(ApplicantId));
    }

    [Theory]
    [InlineData("", "1 Church Lane", "Mother", Emergency, "Your phone number")]
    [InlineData("07000", "  ", "Mother", Emergency, "Your postal address")]
    [InlineData("07000", "1 Church Lane", " ", Emergency, "Your relationship")]
    [InlineData("07000", "1 Church Lane", "Mother", "", "The emergency contact")]
    public async Task Submit_MissingRequiredParentOrChildDetail_IsRefused_AndNothingIsCreated(
        string phone, string address, string relationship, string emergency, string expectedMessageStart)
    {
        using var applicant = Caller(ApplicantId);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            applicant.Service.SubmitAsync(new ApplicantDetails(phone, address), [Child(relationship: relationship, emergency: emergency)]));

        Assert.StartsWith(expectedMessageStart, ex.Message);
        Assert.Empty(applicant.Db.StudentApplications);
    }

    [Fact]
    public async Task Submit_NoChildren_FutureDateOfBirth_OrOverlongSen_AreRefused()
    {
        using var applicant = Caller(ApplicantId);

        await Assert.ThrowsAsync<ArgumentException>(() => applicant.Service.SubmitAsync(Parent(), []));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            applicant.Service.SubmitAsync(Parent(), [Child(dateOfBirth: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            applicant.Service.SubmitAsync(Parent(), [Child(sen: new string('x', ConsentSensitiveDetails.MaxTextLength + 1))]));
        Assert.Empty(applicant.Db.StudentApplications);
    }

    [Fact]
    public async Task Submit_SameNameTwiceInOneSubmission_IgnoringCaseAndSpaces_IsRefused_AndNothingIsCreated()
    {
        using var applicant = Caller(ApplicantId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            applicant.Service.SubmitAsync(Parent(), [Child("Chris Child"), Child("  chris   CHILD ", new DateOnly(2019, 1, 1))]));

        Assert.Empty(applicant.Db.StudentApplications);
    }

    [Fact]
    public async Task Submit_SameNameAsAnOpenOrApprovedApplication_IsRefused()
    {
        var first = await SubmitAsync();
        using (var applicant = Caller(ApplicantId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => applicant.Service.SubmitAsync(Parent(), [Child("CHRIS child")]));
        }

        await PassBothStagesAsync(first);
        using (var applicant = Caller(ApplicantId, Role.Parent))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => applicant.Service.SubmitAsync(Parent(), [Child("Chris Child")]));
        }

        Assert.Single(Caller(AdminId, Role.Admin).Db.StudentApplications);
    }

    [Fact]
    public async Task Submit_SameNameAsAStudentAlreadyLinkedToTheApplicant_IsRefused()
    {
        using var parent = Caller(ExistingParentId, Role.Parent);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            parent.Service.SubmitAsync(Parent(), [Child(" alex  SMITH", new DateOnly(2015, 3, 4))]));

        Assert.Empty(parent.Db.StudentApplications);
    }

    [Fact]
    public async Task Submit_SameNameAfterARejectedApplication_IsAllowed()
    {
        var first = await SubmitAsync();
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await so.Service.SafeguardingRejectAsync(first, "Wrong form");
        }

        var second = await SubmitAsync();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Submit_ChildrenOfDifferentParentsWithTheSameName_AreDifferentChildren()
    {
        await SubmitAsync(ApplicantId, Child("Alex Smith", new DateOnly(2015, 3, 4)));
        await SubmitAsync(OtherApplicantId, Child("Alex Smith", new DateOnly(2015, 3, 4)));

        using var admin = Caller(AdminId, Role.Admin);
        Assert.Equal(2, admin.Db.StudentApplications.Count(a => a.ChildName == "Alex Smith"));
    }

    [Fact]
    public async Task Submit_ByStudentSignIn_Leaver_OrUnresolvedCaller_IsRefused()
    {
        using (var student = Caller(StudentSignInId, Role.Student))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => student.Service.SubmitAsync(Parent(), [Child()]));
        }

        using (var leaver = Caller(LeaverId))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => leaver.Service.SubmitAsync(Parent(), [Child()]));
        }

        using (var nobody = Caller((int?)null))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nobody.Service.SubmitAsync(Parent(), [Child()]));
        }

        Assert.Empty(Caller(AdminId, Role.Admin).Db.StudentApplications);
    }

    // ------------------------------------------------------------------ restricted details

    [Fact]
    public async Task SensitiveDetails_AreVisibleOnlyToTheApplicant_AndToAdminOrSafeguardingWithMfa()
    {
        var id = await SubmitAsync();

        int Visible(CallerScope caller) => caller.Db.StudentApplicationSensitiveDetails.Count(d => d.StudentApplicationId == id);

        using (var c = Caller(ApplicantId)) Assert.Equal(1, Visible(c));
        using (var c = Caller(SafeguardingId, Role.SafeguardingOfficer)) Assert.Equal(1, Visible(c));
        using (var c = Caller(AdminId, Role.Admin)) Assert.Equal(1, Visible(c));
        using (var c = Caller(SafeguardingId, false, Role.SafeguardingOfficer)) Assert.Equal(0, Visible(c));
        using (var c = Caller(AdminId, false, Role.Admin)) Assert.Equal(0, Visible(c));
        using (var c = Caller(TeacherId, Role.Teacher)) Assert.Equal(0, Visible(c));
        using (var c = Caller(ApproverId, Role.Approver)) Assert.Equal(0, Visible(c));
        using (var c = Caller(OtherApplicantId)) Assert.Equal(0, Visible(c));
        using (var c = Caller(ExistingParentId, Role.Parent)) Assert.Equal(0, Visible(c));
        using (var c = Caller((int?)null, Role.Parent)) Assert.Equal(0, Visible(c));
    }

    [Fact]
    public async Task EveryReadOfTheDetails_IsAuditLogged_WithTheReader()
    {
        var id = await SubmitAsync();

        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            var view = await so.Service.GetForSafeguardingReviewAsync(id);
            Assert.Equal(Sen, view.Details!.SpecialEducationalNeeds);
        }

        using (var applicant = Caller(ApplicantId))
        {
            var mine = await applicant.Service.GetMyApplicationsAsync();
            Assert.Equal(Medical, mine.Single().Details!.EmergencyMedicalInfo);
        }

        using var admin = Caller(AdminId, Role.Admin);
        var logs = admin.Db.SensitiveDataAccessLogs.Where(l => l.Entity == nameof(StudentApplicationSensitiveDetails)).ToList();
        Assert.Contains(logs, l => l.UserId == SafeguardingId && l.EntityId == id);
        Assert.Contains(logs, l => l.UserId == ApplicantId && l.EntityId == id);
    }

    [Fact]
    public async Task StaffLists_CarryNoRestrictedDetails_AndWriteNoAuditEntries()
    {
        await SubmitAsync();

        using var so = Caller(SafeguardingId, Role.SafeguardingOfficer);
        var list = await so.Service.ListForSafeguardingAsync();

        Assert.Null(list.Single().Details);
        Assert.Empty(so.Db.SensitiveDataAccessLogs.Where(l => l.Entity == nameof(StudentApplicationSensitiveDetails)));
    }

    [Fact]
    public async Task GetMyApplications_ShowsOnlyTheCallersOwnApplications()
    {
        await SubmitAsync(ApplicantId);
        await SubmitAsync(OtherApplicantId, Child("Other Child"));

        using var applicant = Caller(ApplicantId);
        var mine = await applicant.Service.GetMyApplicationsAsync();

        Assert.Equal(["Chris Child"], mine.Select(a => a.ChildName));
    }

    // ------------------------------------------------------------------ stage 1

    public static TheoryData<int, bool, Role[]> NotSafeguarding => new()
    {
        { AdminId, true, [Role.Admin] },
        { TeacherId, true, [Role.Teacher] },
        { ApproverId, true, [Role.Approver] },
        { ApplicantId, true, [] },
        { ExistingParentId, true, [Role.Parent] },
        { SafeguardingId, false, [Role.SafeguardingOfficer] },
    };

    [Theory]
    [MemberData(nameof(NotSafeguarding))]
    public async Task StageOne_ByAnyoneButASafeguardingOfficerWithMfa_IsRefused_AndChangesNothing(int userId, bool mfa, Role[] roles)
    {
        var id = await SubmitAsync();
        using var caller = Caller(userId, mfa, roles);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.SafeguardingApproveAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.RequestMoreInformationAsync(id, Request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.SafeguardingRejectAsync(id, "No"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListForSafeguardingAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.GetForSafeguardingReviewAsync(id));

        Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview, Load(id).Status);
    }

    [Fact]
    public async Task StageOneApproval_PassesToAdmins_RecordsTheReviewer_EmailsActiveAdmins_AndCreatesNothing()
    {
        var id = await SubmitAsync();
        var before = Counts();
        _notifications.SentRequests.Clear();

        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await so.Service.SafeguardingApproveAsync(id);
        }

        var application = Load(id);
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, application.Status);
        Assert.Equal(SafeguardingId, application.SafeguardingReviewedByUserId);
        Assert.NotNull(application.SafeguardingReviewedAt);
        Assert.Equal(before, Counts());
        Assert.Equal(["user1@example.com", "user3@example.com"], _notifications.SentRequests.Select(r => r.RecipientEmail).Order());
        Assert.Contains("/registration/approve", _notifications.SentRequests[0].Body);
    }

    [Fact]
    public async Task Submission_EmailsEveryActiveSafeguardingOfficer_AndNoOneElse()
    {
        _notifications.SentRequests.Clear();
        await SubmitAsync();

        Assert.Equal(["user2@example.com", "user3@example.com"], _notifications.SentRequests.Select(r => r.RecipientEmail).Order());
        Assert.All(_notifications.SentRequests, r => Assert.Contains("Chris Child", r.Body));
        Assert.Contains("/registration/review", _notifications.SentRequests[0].Body);
    }

    [Fact]
    public async Task MoreInformationLoop_ParentAnswersAndEdits_ReturnsToSafeguarding_ThenCanBeApproved()
    {
        var id = await SubmitAsync();
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => so.Service.RequestMoreInformationAsync(id, " "));
            _notifications.SentRequests.Clear();
            await so.Service.RequestMoreInformationAsync(id, Request);
        }

        Assert.Equal(StudentApplicationStatus.MoreInformationRequested, Load(id).Status);
        var toParent = Assert.Single(_notifications.SentRequests);
        Assert.Equal("user30@example.com", toParent.RecipientEmail);
        Assert.DoesNotContain(Request, toParent.Body);

        // Stage 1 cannot decide it while the parent's answer is awaited.
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => so.Service.SafeguardingApproveAsync(id));
        }

        using (var applicant = Caller(ApplicantId))
        {
            var mine = (await applicant.Service.GetMyApplicationsAsync()).Single();
            Assert.Equal(Request, mine.Details!.InformationRequest);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                applicant.Service.ResubmitAsync(id, Parent(), Child(), " "));
            _notifications.SentRequests.Clear();
            await applicant.Service.ResubmitAsync(id, Parent("07999 999999"), Child(sen: "Dyslexia; reading ruler"), Answer);
        }

        var application = Load(id);
        var details = await LoadDetailsAsync(id);
        Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview, application.Status);
        Assert.Equal("07999 999999", application.ParentPhone);
        Assert.True(application.LastSubmittedAt >= application.SubmittedAt);
        Assert.Equal("Dyslexia; reading ruler", details.SpecialEducationalNeeds);
        Assert.Equal(Answer, details.ParentResponse);
        Assert.Equal(Request, details.InformationRequest);
        Assert.Equal(["user2@example.com", "user3@example.com"], _notifications.SentRequests.Select(r => r.RecipientEmail).Order());
        Assert.All(_notifications.SentRequests, r => Assert.DoesNotContain(Answer, r.Body));

        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await so.Service.SafeguardingApproveAsync(id);
        }

        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, Load(id).Status);
    }

    [Fact]
    public async Task Resubmit_ByAnotherAccount_OrWhenNoInformationWasRequested_IsRefused()
    {
        var id = await SubmitAsync();
        using (var applicant = Caller(ApplicantId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => applicant.Service.ResubmitAsync(id, Parent(), Child(), Answer));
        }

        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await so.Service.RequestMoreInformationAsync(id, Request);
        }

        using (var other = Caller(OtherApplicantId))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.Service.ResubmitAsync(id, Parent(), Child("Hijack"), Answer));
        }

        using (var admin = Caller(AdminId, Role.Admin))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.Service.ResubmitAsync(id, Parent(), Child("Hijack"), Answer));
        }

        Assert.Equal("Chris Child", Load(id).ChildName);
        Assert.Equal(StudentApplicationStatus.MoreInformationRequested, Load(id).Status);
    }

    [Fact]
    public async Task Resubmit_RenamingToAnotherOfTheParentsChildren_IsRefusedAsADuplicate()
    {
        var first = await SubmitAsync(ApplicantId, Child("Chris Child"));
        await SubmitAsync(ApplicantId, Child("Dana Child"));
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await so.Service.RequestMoreInformationAsync(first, Request);
        }

        using var applicant = Caller(ApplicantId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            applicant.Service.ResubmitAsync(first, Parent(), Child("dana  child"), Answer));
    }

    [Fact]
    public async Task StageOneRejection_NeedsComments_TellsTheParentWithoutTheComments_AndCreatesNothing()
    {
        var id = await SubmitAsync();
        var before = Counts();
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => so.Service.SafeguardingRejectAsync(id, ""));
            _notifications.SentRequests.Clear();
            await so.Service.SafeguardingRejectAsync(id, "Duplicate of an earlier paper form (sensitive)");
        }

        Assert.Equal(StudentApplicationStatus.Rejected, Load(id).Status);
        Assert.Equal("Duplicate of an earlier paper form (sensitive)", (await LoadDetailsAsync(id)).DecisionComments);
        var email = Assert.Single(_notifications.SentRequests);
        Assert.Equal("user30@example.com", email.RecipientEmail);
        Assert.DoesNotContain("paper form", email.Body);
        Assert.Equal(before, Counts());
    }

    // ------------------------------------------------------------------ stage 2

    public static TheoryData<int, bool, Role[]> NotAdmin => new()
    {
        { SafeguardingId, true, [Role.SafeguardingOfficer] },
        { TeacherId, true, [Role.Teacher] },
        { ApproverId, true, [Role.Approver] },
        { ApplicantId, true, [] },
        { ExistingParentId, true, [Role.Parent] },
        { AdminId, false, [Role.Admin] },
    };

    [Theory]
    [MemberData(nameof(NotAdmin))]
    public async Task StageTwo_ByAnyoneButAnAdminWithMfa_IsRefused_AndCreatesNothing(int userId, bool mfa, Role[] roles)
    {
        var id = await SubmitAndPassStageOneAsync();
        var before = Counts();
        using var caller = Caller(userId, mfa, roles);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ApproveAsync(id, startingRankId: 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.AdminRejectAsync(id, "No"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListForAdminAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.GetForApprovalAsync(id));

        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, Load(id).Status);
        Assert.Equal(before, Counts());
        Assert.Empty(RolesOf(ApplicantId));
    }

    [Fact]
    public async Task AdminApproval_BeforeSafeguardingReview_IsRefused()
    {
        var id = await SubmitAsync();
        using var admin = Caller(AdminId, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ApproveAsync(id, startingRankId: 1));

        Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview, Load(id).Status);
        Assert.Empty(admin.Db.Students.Where(s => s.Name == "Chris Child"));
    }

    [Fact]
    public async Task AdminApproval_CreatesStudentAtTheChosenRank_GuardianLinkConsentAndParentRole()
    {
        var id = await SubmitAndPassStageOneAsync();
        _notifications.SentRequests.Clear();

        int studentId;
        using (var admin = Caller(AdminId, Role.Admin))
        {
            studentId = await admin.Service.ApproveAsync(id, startingRankId: 2);
        }

        using var verify = Caller(AdminId, Role.Admin);
        var db = verify.Db;
        var student = db.Students.Single(s => s.Id == studentId);
        Assert.Equal("Chris Child", student.Name);
        Assert.Equal(new DateOnly(2016, 5, 6), student.DateOfBirth);
        Assert.Equal(2, student.CurrentRankId);
        Assert.Equal(StudentStatus.Active, student.Status);
        var progress = db.StudentRankProgresses.Single(p => p.StudentId == studentId);
        Assert.Equal(2, progress.RankId);
        Assert.Null(progress.CompletedAt);

        var guardian = db.ParentGuardians.Single(g => g.AppUserId == ApplicantId);
        Assert.Equal("Jane Applicant", guardian.Name);
        Assert.Equal("07000 000001", guardian.Phone);
        Assert.Equal("1 Church Lane\nShrewsbury", guardian.PostalAddress);
        var link = db.StudentGuardianLinks.Single(l => l.StudentId == studentId);
        Assert.Equal(guardian.Id, link.ParentGuardianId);
        Assert.Equal(AdminId, link.CreatedByUserId);
        Assert.Equal("Mother", link.Relationship);

        var consent = db.ConsentRecords.Single(c => c.StudentId == studentId);
        Assert.Equal(ConsentStatus.Approved, consent.Status);
        Assert.Equal(SafeguardingId, consent.ApprovedByUserId);
        Assert.Equal(guardian.Id, consent.SubmittedByParentId);
        Assert.Equal(ApplicantId, consent.SubmittedByUserId);
        Assert.True(consent.ExpiryDate >= DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.True(consent.TransportConsent);
        Assert.False(consent.OffsiteTripConsent);
        var consentDetails = db.ConsentSensitiveDetails.Single(d => d.ConsentRecordId == consent.Id);
        Assert.Equal(Emergency, consentDetails.EmergencyContact);
        Assert.Equal(Medical, consentDetails.EmergencyMedicalInfo);
        Assert.Equal(Dietary, consentDetails.DietaryRequirements);
        Assert.Equal(Sen, consentDetails.SpecialEducationalNeeds);

        var application = db.StudentApplications.Single(a => a.Id == id);
        Assert.Equal(StudentApplicationStatus.Approved, application.Status);
        Assert.Equal(AdminId, application.AdminDecidedByUserId);
        Assert.Equal(SafeguardingId, application.SafeguardingReviewedByUserId);
        Assert.Equal(studentId, application.StudentId);
        Assert.Equal(consent.Id, application.ConsentRecordId);
        Assert.False(application.LinkedToExistingStudent);

        Assert.Equal([Role.Parent], RolesOf(ApplicantId));
        var email = Assert.Single(_notifications.SentRequests);
        Assert.Equal("user30@example.com", email.RecipientEmail);
        Assert.Contains("approved", email.Body);
    }

    [Fact]
    public async Task ApprovedParent_ThenReadsTheirChildsConsentDetails_ThroughTheExistingParentFilter()
    {
        var id = await SubmitAndPassStageOneAsync();
        int studentId;
        using (var admin = Caller(AdminId, Role.Admin))
        {
            studentId = await admin.Service.ApproveAsync(id, startingRankId: 1);
        }

        using var parent = Caller(ApplicantId, Role.Parent);
        Assert.Single(parent.Db.ConsentSensitiveDetails.Where(d => d.ConsentRecord!.StudentId == studentId));
        using var teacherWithoutLink = Caller(OtherApplicantId, Role.Parent);
        Assert.Empty(teacherWithoutLink.Db.ConsentSensitiveDetails.Where(d => d.ConsentRecord!.StudentId == studentId));
    }

    [Fact]
    public async Task AdminApproval_ForAParentWhoAlreadyHasAGuardianRecord_ReusesIt_AndKeepsTheirRoles()
    {
        var id = await SubmitAndPassStageOneAsync(ExistingParentId, Child("Bobby Smith", new DateOnly(2017, 1, 1)));
        var guardiansBefore = Counts().Guardians;

        using (var admin = Caller(AdminId, Role.Admin))
        {
            await admin.Service.ApproveAsync(id, startingRankId: 1);
        }

        using var verify = Caller(AdminId, Role.Admin);
        Assert.Equal(guardiansBefore, verify.Db.ParentGuardians.Count());
        Assert.Equal(2, verify.Db.StudentGuardianLinks.Count(l => l.ParentGuardianId == 1));
        Assert.Equal("07000 000001", verify.Db.ParentGuardians.Single(g => g.Id == 1).Phone);
        Assert.Equal([Role.Parent], RolesOf(ExistingParentId));
    }

    [Fact]
    public async Task AdminApproval_ForATeacherApplyingAsAParent_AddsParentToTheirRoles()
    {
        var id = await SubmitAsync(TeacherId, Child(), Role.Teacher);
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer)) await so.Service.SafeguardingApproveAsync(id);
        using (var admin = Caller(AdminId, Role.Admin)) await admin.Service.ApproveAsync(id, startingRankId: 1);

        Assert.Equal([Role.Teacher, Role.Parent], RolesOf(TeacherId));
    }

    [Fact]
    public async Task OnePersonHoldingBothRoles_MayApproveBothStages_AndEachStageRecordsThem()
    {
        var id = await SubmitAsync();
        using (var both = Caller(BothRolesId, Role.Admin, Role.SafeguardingOfficer))
        {
            await both.Service.SafeguardingApproveAsync(id);
            await both.Service.ApproveAsync(id, startingRankId: 1);
        }

        var application = Load(id);
        Assert.Equal(StudentApplicationStatus.Approved, application.Status);
        Assert.Equal(BothRolesId, application.SafeguardingReviewedByUserId);
        Assert.Equal(BothRolesId, application.AdminDecidedByUserId);
    }

    [Fact]
    public async Task ApprovalPage_ShowsExistingStudentsWithTheSameNameAndDateOfBirth_Only()
    {
        var id = await SubmitAndPassStageOneAsync(OtherApplicantId, Child(" alex smith ", new DateOnly(2015, 3, 4), "Father"));
        using (var admin = Caller(AdminId, Role.Admin))
        {
            admin.Db.Students.Add(new Student
            {
                Id = 102, Name = "Alex Smith", DateOfBirth = new DateOnly(2015, 3, 5), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
            });
            admin.Db.SaveChanges();
        }

        using var approver = Caller(AdminId, Role.Admin);
        var page = await approver.Service.GetForApprovalAsync(id);

        var match = Assert.Single(page.SameNameAndDateOfBirth);
        Assert.Equal(ExistingStudentId, match.StudentId);
        Assert.Equal(["Erin Parent"], match.GuardianNames);
        Assert.Equal([1, 2], page.Ranks.Select(r => r.Id));
        Assert.Equal(Sen, page.Application.Details!.SpecialEducationalNeeds);
    }

    [Fact]
    public async Task LinkToExistingStudent_AddsThisParent_WithoutCreatingASecondStudent()
    {
        var id = await SubmitAndPassStageOneAsync(OtherApplicantId, Child("Alex Smith", new DateOnly(2015, 3, 4), "Father"));
        var studentsBefore = Counts().Students;

        int studentId;
        using (var admin = Caller(AdminId, Role.Admin))
        {
            studentId = await admin.Service.ApproveAsync(id, startingRankId: null, existingStudentId: ExistingStudentId);
        }

        Assert.Equal(ExistingStudentId, studentId);
        using var verify = Caller(AdminId, Role.Admin);
        Assert.Equal(studentsBefore, verify.Db.Students.Count());
        Assert.Equal(2, verify.Db.StudentGuardianLinks.Count(l => l.StudentId == ExistingStudentId));
        Assert.Single(verify.Db.StudentRankProgresses.Where(p => p.StudentId == ExistingStudentId));
        var application = verify.Db.StudentApplications.Single(a => a.Id == id);
        Assert.True(application.LinkedToExistingStudent);
        Assert.Equal(ExistingStudentId, application.StudentId);
        Assert.Equal([Role.Parent], RolesOf(OtherApplicantId));
    }

    [Fact]
    public async Task LinkToExistingStudent_WithADifferentNameOrDateOfBirth_IsRefused_AndCreatesNothing()
    {
        var id = await SubmitAndPassStageOneAsync(OtherApplicantId, Child("Alex Smith", new DateOnly(2015, 3, 9), "Father"));
        var before = Counts();

        using (var admin = Caller(AdminId, Role.Admin))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                admin.Service.ApproveAsync(id, startingRankId: null, existingStudentId: ExistingStudentId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                admin.Service.ApproveAsync(id, startingRankId: null, existingStudentId: 101));
        }

        Assert.Equal(before, Counts());
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, Load(id).Status);
        Assert.Empty(RolesOf(OtherApplicantId));
    }

    [Fact]
    public async Task Approval_NeedsExactlyOneOfRankOrExistingStudent_AndARealRank()
    {
        var id = await SubmitAndPassStageOneAsync();
        using var admin = Caller(AdminId, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.ApproveAsync(id, startingRankId: null));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.ApproveAsync(id, startingRankId: 1, existingStudentId: ExistingStudentId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ApproveAsync(id, startingRankId: 99));

        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, Load(id).Status);
    }

    [Fact]
    public async Task Approval_IsAtomic_AFailingWriteLeavesNoStudentLinkGuardianConsentOrRole()
    {
        var id = await SubmitAndPassStageOneAsync();

        // Force the final save to fail: the next consent record id is 2, whose details row would
        // take id 2 (DetailsIdFor), and a details row with id 2 already exists for record 1.
        using (var admin = Caller(AdminId, Role.Admin))
        {
            admin.Db.ConsentRecords.Add(new ConsentRecord
            {
                Id = 1, StudentId = ExistingStudentId, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31),
                ExpiryDate = new DateOnly(2026, 12, 31), PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true,
                DataSharingConsent = true, Status = ConsentStatus.Approved, SubmittedByParentId = 1
            });
            admin.Db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 2, ConsentRecordId = 1, EmergencyContact = "x" });
            admin.Db.SaveChanges();
        }

        var before = Counts();
        using (var admin = Caller(AdminId, Role.Admin))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => admin.Service.ApproveAsync(id, startingRankId: 1));
        }

        Assert.Equal(before, Counts());
        Assert.Empty(RolesOf(ApplicantId));
        var application = Load(id);
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, application.Status);
        Assert.Null(application.StudentId);
        Assert.Null(application.AdminDecidedByUserId);
    }

    [Fact]
    public async Task Approval_IsRefused_WhenTheApplicantWasLinkedToAStudentWithThatNameSinceApplying()
    {
        var id = await SubmitAndPassStageOneAsync(ApplicantId, Child("Alex Smith", new DateOnly(2016, 1, 1)));
        using (var seed = Caller(AdminId, Role.Admin))
        {
            // Meanwhile an Admin linked the applicant to the existing Alex Smith directly.
            seed.Db.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Jane Applicant", ContactInfo = "user30@example.com", IsPrimary = true, AppUserId = ApplicantId });
            seed.Db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = ExistingStudentId, ParentGuardianId = 2, CreatedByUserId = AdminId });
            seed.Db.SaveChanges();
        }

        using var admin = Caller(AdminId, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ApproveAsync(id, startingRankId: 1));
        Assert.Equal(StudentApplicationStatus.AwaitingAdminApproval, Load(id).Status);
    }

    [Fact]
    public async Task Approval_IsRefused_WhenTheApplicantHasSinceLeft()
    {
        var id = await SubmitAndPassStageOneAsync();
        using (var admin = Caller(AdminId, Role.Admin))
        {
            var user = admin.Db.AppUsers.Single(u => u.Id == ApplicantId);
            admin.Db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            admin.Db.SaveChanges();
        }

        using var approver = Caller(AdminId, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => approver.Service.ApproveAsync(id, startingRankId: 1));
        Assert.Empty(RolesOf(ApplicantId));
    }

    [Fact]
    public async Task AdminRejection_NeedsComments_TellsTheParent_AndCreatesNothing()
    {
        var id = await SubmitAndPassStageOneAsync();
        var before = Counts();
        using (var admin = Caller(AdminId, Role.Admin))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.AdminRejectAsync(id, " "));
            _notifications.SentRequests.Clear();
            await admin.Service.AdminRejectAsync(id, "We have no space in this group");
        }

        var application = Load(id);
        Assert.Equal(StudentApplicationStatus.Rejected, application.Status);
        Assert.Equal(AdminId, application.AdminDecidedByUserId);
        Assert.Equal(before, Counts());
        Assert.Empty(RolesOf(ApplicantId));
        Assert.Equal("user30@example.com", Assert.Single(_notifications.SentRequests).RecipientEmail);
    }

    [Fact]
    public async Task NoEmailAtAnyStage_CarriesSensitiveDetailsRequestsOrAnswers()
    {
        _notifications.SentRequests.Clear();
        var id = await SubmitAsync();
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer)) await so.Service.RequestMoreInformationAsync(id, Request);
        using (var applicant = Caller(ApplicantId)) await applicant.Service.ResubmitAsync(id, Parent(), Child(), Answer);
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer)) await so.Service.SafeguardingApproveAsync(id, "Checked the SEN plan (sensitive)");
        using (var admin = Caller(AdminId, Role.Admin)) await admin.Service.ApproveAsync(id, startingRankId: 1);

        Assert.True(_notifications.SentRequests.Count >= 6);
        foreach (var email in _notifications.SentRequests)
        {
            foreach (var secret in new[] { Medical, Dietary, Sen, Emergency, Request, Answer, "sensitive" })
            {
                Assert.DoesNotContain(secret, email.Body);
                Assert.DoesNotContain(secret, email.Subject);
            }
        }

        Assert.DoesNotContain(_notifications.SentRequests, r => r.RecipientEmail == "user4@example.com");
    }

    [Fact]
    public async Task AFailingEmail_NeitherThrowsNorUndoesTheDecision()
    {
        var id = await SubmitAsync();
        using (var so = Caller(new FakeCurrentUserContext(SafeguardingId, Role.SafeguardingOfficer), new ThrowingNotificationService()))
        {
            await so.Service.SafeguardingApproveAsync(id);
        }

        using (var admin = Caller(new FakeCurrentUserContext(AdminId, Role.Admin), new ThrowingNotificationService()))
        {
            await admin.Service.ApproveAsync(id, startingRankId: 1);
        }

        Assert.Equal(StudentApplicationStatus.Approved, Load(id).Status);
    }

    [Fact]
    public async Task HomePageCounts_AreRestrictedToEachStagesRole()
    {
        await SubmitAsync();
        await SubmitAndPassStageOneAsync(ApplicantId, Child("Dana Child"));

        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer))
        {
            Assert.Equal(1, await so.Service.CountAwaitingSafeguardingReviewAsync());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => so.Service.CountAwaitingAdminApprovalAsync());
        }

        using var admin = Caller(AdminId, Role.Admin);
        Assert.Equal(1, await admin.Service.CountAwaitingAdminApprovalAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.Service.CountAwaitingSafeguardingReviewAsync());
    }

    private async Task PassBothStagesAsync(int id)
    {
        using (var so = Caller(SafeguardingId, Role.SafeguardingOfficer)) await so.Service.SafeguardingApproveAsync(id);
        using (var admin = Caller(AdminId, Role.Admin)) await admin.Service.ApproveAsync(id, startingRankId: 1);
    }

    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("mail server down");
    }
}
