using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Progress;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The parent consent form and the teacher read-only view (functional.md FR-013, FR-015;
/// adr/0006-parent-consent-and-teacher-read-access.md): a linked Parent reads and submits for their
/// own child only; another child's Parent can do neither; a Teacher reads but has no write path;
/// the Approver role gets nothing; a Safeguarding Officer/Admin approval updates the Student and
/// satisfies the FR-003 consent block in <see cref="CompletionService"/>; a rejected form can be
/// resubmitted, and an update after approval is a new Pending submission, not an overwrite.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="ConsentRecordServiceTests"/>.
/// </summary>
public sealed class ParentConsentServiceTests : IDisposable
{
    private const int AdminUserId = 1;
    private const int SafeguardingUserId = 2;
    private const int TeacherUserId = 10;
    private const int ApproverUserId = 20;
    private const int ParentOneUserId = 30;
    private const int ParentTwoUserId = 31;
    private const int StudentOneId = 100;
    private const int StudentTwoId = 200;

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public ParentConsentServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-parent-consent-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(AdminUserId, Role.Admin);
        schema.Context.Database.EnsureCreated();
        SeedReferenceData(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(
        ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser, InMemoryBlobStorage blobStorage) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public ParentConsentService Parent => new(Context, currentUser);
        public ConsentRecordService Records => new(Context, currentUser);
        public StudentConsentViewService View => new(Context, currentUser);
        public CompletionService Completion =>
            new(Context, currentUser, new PromotionService(Context), new CertificateService(Context, blobStorage));

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

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context, currentUser, _blobStorage);
    }

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", Order = 1, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "First Aid", Order = 2, IsActive = true });
        context.AppUsers.Add(new AppUser { Id = AdminUserId, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = SafeguardingUserId, DisplayName = "Safeguarding One", Email = "sg@example.com" });
        context.AppUsers.Add(new AppUser { Id = TeacherUserId, DisplayName = "Teacher One", Email = "teacher@example.com" });
        context.AppUsers.Add(new AppUser { Id = ApproverUserId, DisplayName = "Approver One", Email = "approver@example.com" });
        context.AppUsers.Add(new AppUser { Id = ParentOneUserId, DisplayName = "Jane Parent", Email = "jane@example.com" });
        context.AppUsers.Add(new AppUser { Id = ParentTwoUserId, DisplayName = "Pat Parent", Email = "pat@example.com" });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "jane@example.com", IsPrimary = true, AppUserId = ParentOneUserId });
        context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Pat Parent", ContactInfo = "pat@example.com", IsPrimary = true, AppUserId = ParentTwoUserId });
        context.Students.Add(new Student
        {
            Id = StudentOneId,
            Name = "Alex Studnet",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.Students.Add(new Student
        {
            Id = StudentTwoId,
            Name = "Sam Student",
            DateOfBirth = new DateOnly(2013, 6, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = 1, StudentId = StudentOneId, RankId = 1, StartedAt = DateTime.UtcNow.AddDays(-30), CompletedAt = null
        });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = StudentOneId, ParentGuardianId = 1, CreatedByUserId = AdminUserId });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = StudentTwoId, ParentGuardianId = 2, CreatedByUserId = AdminUserId });
        context.SaveChanges();
    }

    private static ParentConsentForm Form(
        string name = "Alex Student", string emergencyContact = "Jane Parent, 07000 000000", string? medical = "Asthma") =>
        new(name, new DateOnly(2012, 2, 3), true, true, false, true, emergencyContact, medical, "No nuts", "Dyslexia");

    private async Task<int> SubmitAsParentOneAsync(ParentConsentForm? form = null)
    {
        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var record = await parent.Parent.SubmitAsync(StudentOneId, form ?? Form());
        return record.Id;
    }

    private async Task DecideAsync(int recordId, bool approve, string? comments = null)
    {
        using var sg = CreateContext(SafeguardingUserId, Role.SafeguardingOfficer);
        await sg.Records.DecideAsync(recordId, approve, comments);
    }

    private int SensitiveReadCount()
    {
        using var verify = CreateContext(AdminUserId, Role.Admin);
        return verify.Context.SensitiveDataAccessLogs.Count(l => l.Entity == nameof(ConsentSensitiveDetails));
    }

    // --- Linked parent: submit and read own child ------------------------------------------------

    [Fact]
    public async Task LinkedParent_Submit_CreatesPendingRecordWithDetails_ForOwnChild()
    {
        var recordId = await SubmitAsParentOneAsync();

        using var verify = CreateContext(AdminUserId, Role.Admin);
        var record = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == recordId);
        Assert.Equal(ConsentStatus.Pending, record.Status);
        Assert.Equal(StudentOneId, record.StudentId);
        Assert.Equal(1, record.SubmittedByParentId);
        Assert.Equal(ParentOneUserId, record.SubmittedByUserId);
        Assert.Equal("Alex Student", record.StudentName);
        Assert.Equal(new DateOnly(2012, 2, 3), record.StudentDateOfBirth);
        Assert.Equal(ConsentYear.EndFor(record.PeriodStart), record.ExpiryDate);
        Assert.Equal(8, record.ExpiryDate.Month);
        Assert.Equal(31, record.ExpiryDate.Day);
        Assert.Equal(record.ExpiryDate, record.PeriodEnd);

        var details = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == recordId);
        Assert.Equal("Asthma", details.EmergencyMedicalInfo);
        Assert.Equal("No nuts", details.DietaryRequirements);
        Assert.Equal("Dyslexia", details.SpecialEducationalNeeds);

        // Submitting does not touch the Student — only approval does (FR-013).
        Assert.Equal("Alex Studnet", (await verify.Context.Students.SingleAsync(s => s.Id == StudentOneId)).Name);
    }

    [Fact]
    public async Task Submit_SavesTransportAndOffsiteTripsSeparately_AndDietaryAndSenSeparately()
    {
        var recordId = await SubmitAsParentOneAsync(new ParentConsentForm(
            "Alex Student", new DateOnly(2012, 2, 3), true, false, true, true, "Jane Parent", null, "Vegetarian", null));
        await DecideAsync(recordId, approve: true);
        var secondId = await SubmitAsParentOneAsync(new ParentConsentForm(
            "Alex Student", new DateOnly(2012, 2, 3), true, true, false, true, "Jane Parent", null, null, "Needs a quiet space"));

        using var verify = CreateContext(AdminUserId, Role.Admin);
        var first = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == recordId);
        Assert.False(first.TransportConsent);
        Assert.True(first.OffsiteTripConsent);
        var second = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == secondId);
        Assert.True(second.TransportConsent);
        Assert.False(second.OffsiteTripConsent);

        var d1 = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == recordId);
        Assert.Equal("Vegetarian", d1.DietaryRequirements);
        Assert.Null(d1.SpecialEducationalNeeds);
        var d2 = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == secondId);
        Assert.Null(d2.DietaryRequirements);
        Assert.Equal("Needs a quiet space", d2.SpecialEducationalNeeds);

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var child = Assert.Single(await parent.Parent.GetMyChildrenAsync());
        Assert.True(child.LatestSubmission?.TransportConsent);
        Assert.False(child.LatestSubmission?.OffsiteTripConsent);
        Assert.Null(child.LatestDetails?.DietaryRequirements);
        Assert.Equal("Needs a quiet space", child.LatestDetails?.SpecialEducationalNeeds);
    }

    [Fact]
    public async Task Submit_DietaryOrSenOverTheLimit_IsRefused_AndNothingIsCreated()
    {
        var limit = ConsentSensitiveDetails.MaxTextLength;
        using (var parent = CreateContext(ParentOneUserId, Role.Parent))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => parent.Parent.SubmitAsync(StudentOneId, new ParentConsentForm(
                "Alex Student", new DateOnly(2012, 2, 3), true, true, true, true, "Jane", null, new string('a', limit + 1), null)));
            await Assert.ThrowsAsync<ArgumentException>(() => parent.Parent.SubmitAsync(StudentOneId, new ParentConsentForm(
                "Alex Student", new DateOnly(2012, 2, 3), true, true, true, true, "Jane", null, null, new string('a', limit + 1))));
        }

        using var verify = CreateContext(AdminUserId, Role.Admin);
        Assert.Empty(await verify.Context.ConsentRecords.ToListAsync());

        // Exactly at the limit is accepted.
        var id = await SubmitAsParentOneAsync(new ParentConsentForm(
            "Alex Student", new DateOnly(2012, 2, 3), true, true, true, true, "Jane", null, new string('a', limit), new string('b', limit)));
        var details = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == id);
        Assert.Equal(limit, details.DietaryRequirements!.Length);
        Assert.Equal(limit, details.SpecialEducationalNeeds!.Length);
    }

    [Fact]
    public async Task StaffRecord_DietaryOrSenOverTheLimit_IsRefused()
    {
        var tooLong = new string('a', ConsentSensitiveDetails.MaxTextLength + 1);
        using var admin = CreateContext(AdminUserId, Role.Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => admin.Records.RecordAsync(
            StudentOneId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, true, 1, "Contact", null, tooLong, null));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.Records.RecordAsync(
            StudentOneId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, true, 1, "Contact", null, null, tooLong));
        Assert.Empty(await admin.Context.ConsentRecords.ToListAsync());
    }

    [Fact]
    public async Task LinkedParent_Read_ListsOnlyOwnChild_WithLatestDetails_AndIsAudited()
    {
        await SubmitAsParentOneAsync();
        var auditBefore = SensitiveReadCount();

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var children = await parent.Parent.GetMyChildrenAsync();

        var child = Assert.Single(children);
        Assert.Equal(StudentOneId, child.StudentId);
        Assert.Equal(ConsentStatus.Pending, child.LatestSubmission?.Status);
        Assert.Null(child.CurrentConsent);
        Assert.Equal("Asthma", child.LatestDetails?.EmergencyMedicalInfo);
        Assert.Equal(auditBefore + 1, SensitiveReadCount());
    }

    // --- Another child's parent: neither read nor write ------------------------------------------

    [Fact]
    public async Task ParentOfAnotherChild_CannotSubmit_ForThatChild_AndNothingIsCreated()
    {
        using var otherParent = CreateContext(ParentTwoUserId, Role.Parent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => otherParent.Parent.SubmitAsync(StudentOneId, Form()));

        using var verify = CreateContext(AdminUserId, Role.Admin);
        Assert.Empty(verify.Context.ConsentRecords.Where(c => c.StudentId == StudentOneId));
        Assert.Empty(verify.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public async Task ParentOfAnotherChild_CannotRead_ThatChildsDetails()
    {
        await SubmitAsParentOneAsync();

        using var otherParent = CreateContext(ParentTwoUserId, Role.Parent);
        var children = await otherParent.Parent.GetMyChildrenAsync();

        var child = Assert.Single(children);
        Assert.Equal(StudentTwoId, child.StudentId);
        Assert.Null(child.LatestDetails);
        Assert.Empty(otherParent.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public async Task ParentWhoHasLeft_CannotReadOrSubmit()
    {
        using (var admin = CreateContext(AdminUserId, Role.Admin))
        {
            var user = await admin.Context.AppUsers.SingleAsync(u => u.Id == ParentOneUserId);
            admin.Context.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            await admin.Context.SaveChangesAsync();
        }

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => parent.Parent.GetMyChildrenAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => parent.Parent.SubmitAsync(StudentOneId, Form()));
    }

    [Fact]
    public async Task StaffLeaverWhoIsAlsoAParent_StillUsesTheParentForm()
    {
        // FR-017 clarified 23/10/2026: their HasLeft records leaving the staff, so parent access stays.
        using (var admin = CreateContext(AdminUserId, Role.Admin))
        {
            admin.Context.UserRoles.Add(new UserRole { UserId = ParentOneUserId, Role = Role.Teacher });
            var user = await admin.Context.AppUsers.SingleAsync(u => u.Id == ParentOneUserId);
            admin.Context.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            await admin.Context.SaveChangesAsync();
        }

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var children = await parent.Parent.GetMyChildrenAsync();
        Assert.Contains(children, c => c.StudentId == StudentOneId);
        await parent.Parent.SubmitAsync(StudentOneId, Form());
    }

    // --- Other roles: no parent write path; teacher reads only ------------------------------------

    [Theory]
    [InlineData(TeacherUserId, Role.Teacher)]
    [InlineData(ApproverUserId, Role.Approver)]
    [InlineData(AdminUserId, Role.Admin)]
    public async Task NonParent_CannotUseTheParentForm(int userId, Role role)
    {
        using var caller = CreateContext(userId, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Parent.SubmitAsync(StudentOneId, Form()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Parent.GetMyChildrenAsync());
    }

    [Fact]
    public async Task Teacher_CanReadCurrentApprovedDetails_AndTheReadIsAudited()
    {
        var recordId = await SubmitAsParentOneAsync();
        await DecideAsync(recordId, approve: true);
        var auditBefore = SensitiveReadCount();

        using var teacher = CreateContext(TeacherUserId, Role.Teacher);
        var detail = await teacher.View.GetStudentAsync(StudentOneId);

        Assert.NotNull(detail);
        Assert.Equal(recordId, detail.CurrentConsent?.ConsentRecordId);
        Assert.Equal("Asthma", detail.CurrentDetails?.EmergencyMedicalInfo);
        Assert.Equal(auditBefore + 1, SensitiveReadCount());

        using var verify = CreateContext(AdminUserId, Role.Admin);
        var lastLog = verify.Context.SensitiveDataAccessLogs.OrderByDescending(l => l.Id).First();
        Assert.Equal(TeacherUserId, lastLog.UserId);
        Assert.Equal(recordId, lastLog.EntityId);
    }

    [Fact]
    public async Task Teacher_DoesNotSeePendingDetails_OnlyReviewedOnes()
    {
        await SubmitAsParentOneAsync();

        using var teacher = CreateContext(TeacherUserId, Role.Teacher);
        var detail = await teacher.View.GetStudentAsync(StudentOneId);

        Assert.NotNull(detail);
        Assert.Null(detail.CurrentDetails);
        Assert.Equal(ConsentStatus.Pending, detail.LatestSubmissionStatus);
    }

    [Fact]
    public async Task Teacher_CannotWrite_ConsentOrDecide()
    {
        var recordId = await SubmitAsParentOneAsync();

        using var teacher = CreateContext(TeacherUserId, Role.Teacher);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Records.DecideAsync(recordId, approve: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Records.RecordAsync(
            StudentOneId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, true, 1, "Contact", null, null, null));
    }

    [Theory]
    [InlineData(ApproverUserId, Role.Approver)]
    [InlineData(ParentOneUserId, Role.Parent)]
    [InlineData(null, Role.Student)]
    public async Task NonStaff_CannotUseTheTeacherView(int? userId, Role role)
    {
        using var caller = CreateContext(userId, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.View.GetStudentAsync(StudentOneId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.View.ListActiveStudentsAsync());
    }

    [Fact]
    public async Task ApproverOnlyUser_ReadsNoSensitiveDetailsAtAll()
    {
        var recordId = await SubmitAsParentOneAsync();
        await DecideAsync(recordId, approve: true);

        using var approver = CreateContext(ApproverUserId, Role.Approver);
        Assert.Empty(approver.Context.ConsentSensitiveDetails.ToList());
        Assert.Empty(approver.Context.DbsChecks.ToList());
    }

    // --- Approval, rejection and resubmission ------------------------------------------------------

    [Fact]
    public async Task Approval_UpdatesStudentDetails_AndLiftsTheConsentBlockOnCompletion()
    {
        using (var teacher = CreateContext(TeacherUserId, Role.Teacher))
        {
            await Assert.ThrowsAsync<ConsentBlockedException>(() => teacher.Completion.MarkCompleteAsync(StudentOneId, 1));
        }

        var recordId = await SubmitAsParentOneAsync();

        // Still blocked while the submission is only Pending.
        using (var teacher = CreateContext(TeacherUserId, Role.Teacher))
        {
            await Assert.ThrowsAsync<ConsentBlockedException>(() => teacher.Completion.MarkCompleteAsync(StudentOneId, 1));
        }

        await DecideAsync(recordId, approve: true);

        using (var verify = CreateContext(AdminUserId, Role.Admin))
        {
            var student = await verify.Context.Students.SingleAsync(s => s.Id == StudentOneId);
            Assert.Equal("Alex Student", student.Name);
            Assert.Equal(new DateOnly(2012, 2, 3), student.DateOfBirth);
            Assert.Equal(StudentStatus.Active, student.Status);
            Assert.Equal(1, student.CurrentRankId);
        }

        using (var teacher = CreateContext(TeacherUserId, Role.Teacher))
        {
            var completion = await teacher.Completion.MarkCompleteAsync(StudentOneId, 1);
            Assert.Equal(StudentOneId, completion.StudentId);
        }

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var child = Assert.Single(await parent.Parent.GetMyChildrenAsync());
        Assert.Equal(recordId, child.CurrentConsent?.ConsentRecordId);
    }

    [Fact]
    public async Task Rejection_LeavesStudentUnchanged_ShowsCommentsToParent_AndResubmissionWorks()
    {
        var rejectedId = await SubmitAsParentOneAsync(Form(emergencyContact: "Jane"));
        await DecideAsync(rejectedId, approve: false, comments: "Please add a phone number.");

        using (var parent = CreateContext(ParentOneUserId, Role.Parent))
        {
            var child = Assert.Single(await parent.Parent.GetMyChildrenAsync());
            Assert.Equal(ConsentStatus.Rejected, child.LatestSubmission?.Status);
            Assert.Equal("Please add a phone number.", child.LatestSubmission?.DecisionComments);
            Assert.Equal("Jane", child.LatestDetails?.EmergencyContact);
        }

        using (var verify = CreateContext(AdminUserId, Role.Admin))
        {
            Assert.Equal("Alex Studnet", (await verify.Context.Students.SingleAsync(s => s.Id == StudentOneId)).Name);
        }

        var resubmittedId = await SubmitAsParentOneAsync(Form(emergencyContact: "Jane Parent, 07000 000000"));
        Assert.NotEqual(rejectedId, resubmittedId);
        await DecideAsync(resubmittedId, approve: true);

        using var check = CreateContext(AdminUserId, Role.Admin);
        var records = await check.Context.ConsentRecords.Where(c => c.StudentId == StudentOneId).OrderBy(c => c.Id).ToListAsync();
        Assert.Equal([ConsentStatus.Rejected, ConsentStatus.Approved], records.Select(r => r.Status));
        Assert.Equal("Alex Student", (await check.Context.Students.SingleAsync(s => s.Id == StudentOneId)).Name);
    }

    [Fact]
    public async Task SecondSubmission_WhileOneIsPending_IsRefused()
    {
        await SubmitAsParentOneAsync();

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => parent.Parent.SubmitAsync(StudentOneId, Form()));

        using var verify = CreateContext(AdminUserId, Role.Admin);
        Assert.Single(verify.Context.ConsentRecords.Where(c => c.StudentId == StudentOneId));
    }

    [Fact]
    public async Task UpdateAfterApproval_CreatesNewPending_AndTheApprovedRecordStaysCurrentAndUnchanged()
    {
        var approvedId = await SubmitAsParentOneAsync();
        await DecideAsync(approvedId, approve: true);

        var updatedId = await SubmitAsParentOneAsync(Form(medical: "Asthma; new inhaler"));

        using (var verify = CreateContext(AdminUserId, Role.Admin))
        {
            var approved = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == approvedId);
            Assert.Equal(ConsentStatus.Approved, approved.Status);
            var approvedDetails = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == approvedId);
            Assert.Equal("Asthma", approvedDetails.EmergencyMedicalInfo);

            var updated = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == updatedId);
            Assert.Equal(ConsentStatus.Pending, updated.Status);
        }

        // Consent stays in place while the update awaits review.
        using (var teacher = CreateContext(TeacherUserId, Role.Teacher))
        {
            await teacher.Completion.MarkCompleteAsync(StudentOneId, 1);
            var detail = await teacher.View.GetStudentAsync(StudentOneId);
            Assert.Equal(approvedId, detail?.CurrentConsent?.ConsentRecordId);
            Assert.Equal("Asthma", detail?.CurrentDetails?.EmergencyMedicalInfo);
        }

        using var parent = CreateContext(ParentOneUserId, Role.Parent);
        var child = Assert.Single(await parent.Parent.GetMyChildrenAsync());
        Assert.Equal(approvedId, child.CurrentConsent?.ConsentRecordId);
        Assert.Equal(updatedId, child.LatestSubmission?.ConsentRecordId);
        Assert.Equal("Asthma; new inhaler", child.LatestDetails?.EmergencyMedicalInfo);
    }

    [Fact]
    public async Task Submit_RejectsBlankNameOrContact_AndFutureDateOfBirth()
    {
        using var parent = CreateContext(ParentOneUserId, Role.Parent);

        await Assert.ThrowsAsync<ArgumentException>(() => parent.Parent.SubmitAsync(StudentOneId, Form(name: "  ")));
        await Assert.ThrowsAsync<ArgumentException>(() => parent.Parent.SubmitAsync(StudentOneId, Form(emergencyContact: "")));
        await Assert.ThrowsAsync<ArgumentException>(() => parent.Parent.SubmitAsync(StudentOneId,
            Form() with { StudentDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2) }));
    }

    [Fact]
    public async Task ParentSubmission_AfterStaffEnteredRecords_GetsNonCollidingIds()
    {
        // The Admin "on behalf" path and the parent path share one id sequence; a Parent's filtered
        // view of ConsentSensitiveDetails must not cause a key collision.
        using (var admin = CreateContext(AdminUserId, Role.Admin))
        {
            var staff = await admin.Records.RecordAsync(
                StudentTwoId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
                true, true, true, true, 2, "Pat Parent", null, null, null);
            Assert.Equal(1, staff.Id);
        }

        var parentRecordId = await SubmitAsParentOneAsync();
        Assert.Equal(2, parentRecordId);

        using var verify = CreateContext(AdminUserId, Role.Admin);
        Assert.Equal(2, verify.Context.ConsentSensitiveDetails.ToList().Count);
    }
}
