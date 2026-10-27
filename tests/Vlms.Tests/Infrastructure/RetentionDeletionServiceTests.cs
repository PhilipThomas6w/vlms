using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The retention purge's deletions (docs/adr/0014-retention-purge.md): who may delete, the active
/// chapter only, the still-due recheck, the typed-name confirmation, what a student's deletion removes,
/// the parent and staff account rules, and the deletion log. Real SQLite in memory through DI, as in
/// <see cref="RecordsDueForDeletionServiceTests"/>, so the chapter and sensitive-data query filters,
/// the read-audit interceptor and the database's cascades all run.
/// </summary>
public sealed class RetentionDeletionServiceTests : IDisposable
{
    private const int Shrewsbury = Chapter.ShrewsburyId;
    private const int Coventry = 2;

    private const int AdminId = 1;
    private const int TeacherId = 10;
    private const int ChildAccountId = 50;
    private const int ParentAccountId = 60;

    private const int OldStudentId = 100;      // left 4 years ago: due
    private const int ActiveStudentId = 101;   // still in the programme
    private const int GuardianId = 200;

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public RetentionDeletionServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-retention-delete-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var seed = CreateContext(TestSeedUserContext.Instance);
        var db = seed.Context;
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = _now, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        db.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "badges/1.png" });

        AddUser(db, AdminId, "Admin One", (Role.Admin, Shrewsbury));
        AddUser(db, TeacherId, "Teacher Ten", (Role.Teacher, Shrewsbury));
        AddUser(db, ChildAccountId, "Alex Old", (Role.Student, Shrewsbury));
        AddUser(db, ParentAccountId, "Pat Parent", (Role.Parent, Shrewsbury));

        db.Students.Add(NewStudent(OldStudentId, "Alex Old", StudentStatus.Inactive, _now.AddYears(-4), ChildAccountId));
        db.ParentGuardians.Add(new ParentGuardian { Id = GuardianId, Name = "Pat Parent", ContactInfo = "pat@example.com", IsPrimary = true, AppUserId = ParentAccountId });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = OldStudentId, ParentGuardianId = GuardianId, CreatedByUserId = AdminId });

        db.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = 300, StudentId = OldStudentId, LessonId = 1, CompletedByUserId = TeacherId, CompletedAt = _now.AddYears(-5), IsReversed = false
        });
        db.Certificates.Add(new Certificate { Id = 400, StudentLessonCompletionId = 300, GeneratedAt = _now.AddYears(-5), BlobKey = "certificates/100/300.pdf" });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 1, StudentId = OldStudentId, RankId = 1, StartedAt = _now.AddYears(-6) });
        db.StudentBadges.Add(new StudentBadge { Id = 1, StudentId = OldStudentId, RankBadgeId = 1, AwardedAt = _now.AddYears(-5) });
        db.ChapterEvents.Add(new ChapterEvent { Id = 1, Date = DateOnly.FromDateTime(_now.AddYears(-5)), Title = "Chapter meeting", CreatedByUserId = AdminId, CreatedAt = _now.AddYears(-5) });
        db.AttendanceRegisters.Add(new AttendanceRegister
        {
            Id = 1, ChapterEventId = 1, RankId = 1, MeetingDate = DateOnly.FromDateTime(_now.AddYears(-5)), TakenByUserId = TeacherId, TakenAt = _now.AddYears(-5)
        });
        db.AttendanceMarks.Add(new AttendanceMark { Id = 1, AttendanceRegisterId = 1, StudentId = OldStudentId, MeetingDate = DateOnly.FromDateTime(_now.AddYears(-5)), Present = true });
        db.ConsentRecords.Add(NewConsent(500, OldStudentId, GuardianId));
        db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 500, ConsentRecordId = 500, EmergencyContact = "Pat 07000 000000" });
        db.GuardianInvites.Add(new GuardianInvite
        {
            Id = 1, StudentId = OldStudentId, ParentGuardianId = GuardianId, CreatedByUserId = AdminId,
            CreatedAt = DateTimeOffset.UtcNow.AddYears(-6), ExpiresAt = DateTimeOffset.UtcNow.AddYears(-6).AddDays(7)
        });

        // The student still in the programme (no guardian link unless a test adds one).
        db.Students.Add(NewStudent(ActiveStudentId, "Casey Current", StudentStatus.Active, null, null));
        db.SaveChanges();

        _blobs.UploadAsync("certificates/100/300.pdf", [1, 2, 3]).GetAwaiter().GetResult();
    }

    public void Dispose() => _anchorConnection.Dispose();

    // ---- who may delete ------------------------------------------------------------------------

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.NationalAdministrator)]
    public async Task OnlyAnAdmin_CanDelete(Role role)
    {
        using var ctx = CreateContext(new FakeCurrentUserContext(AdminId, role));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(ctx).DeleteAsync(StudentRequest()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(ctx).DeleteAllAsync([StudentRequest()]));
        Assert.True(StudentExists(OldStudentId));
    }

    [Fact]
    public async Task AnAdminWithoutTwoFactorAuthentication_IsRefused()
    {
        using var ctx = CreateContext(new FakeCurrentUserContext(AdminId, hasMfaEnabled: false, Role.Admin));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(ctx).DeleteAsync(StudentRequest()));
        Assert.True(StudentExists(OldStudentId));
    }

    [Fact]
    public async Task AnAdminWithNoActiveChapter_IsRefused()
    {
        using var ctx = CreateContext(new FakeCurrentUserContext(AdminId, Role.Admin) { ActiveChapterId = null });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(ctx).DeleteAsync(StudentRequest()));
    }

    [Fact]
    public async Task AnotherChaptersRecord_IsRefused_EvenWhenDue()
    {
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            seed.Context.Students.Add(NewStudent(900, "Coventry Old", StudentStatus.Inactive, _now.AddYears(-5), null, Coventry));
            seed.Context.SaveChanges();
        }

        using var ctx = CreateContext(Admin());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.Student, 900, "Coventry Old")));

        Assert.True(StudentExists(900));
    }

    // ---- confirmation and the still-due recheck ------------------------------------------------

    [Fact]
    public async Task AWrongName_DeletesNothing()
    {
        using var ctx = CreateContext(Admin());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.Student, OldStudentId, "Alex")));

        Assert.Contains("does not match", ex.Message);
        Assert.True(StudentExists(OldStudentId));
        Assert.Equal(0, Count(db => db.DeletionLog));
    }

    [Fact]
    public async Task TheName_IsMatchedIgnoringCaseAndSurroundingSpaces()
    {
        using var ctx = CreateContext(Admin());

        await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.Student, OldStudentId, "  alex OLD "));

        Assert.False(StudentExists(OldStudentId));
    }

    [Fact]
    public async Task ARecordNotDue_IsRefused()
    {
        using var ctx = CreateContext(Admin());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.Student, ActiveStudentId, "Casey Current")));

        Assert.True(StudentExists(ActiveStudentId));
    }

    [Fact]
    public async Task ARecordNoLongerDueAtTheMomentOfDeletion_IsRefused()
    {
        // The page was loaded while the student was due; then their leaving date changed.
        using var ctx = CreateContext(Admin());
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            var student = seed.Context.Students.Single(s => s.Id == OldStudentId);
            seed.Context.Entry(student).Property(s => s.LeftAt).CurrentValue = _now.AddYears(-1);
            seed.Context.SaveChanges();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(ctx).DeleteAsync(StudentRequest()));

        Assert.Contains("no longer due", ex.Message);
        Assert.True(StudentExists(OldStudentId));
    }

    // ---- what a student's deletion removes -----------------------------------------------------

    [Fact]
    public async Task DeletingAStudent_RemovesTheirWholeRecord()
    {
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.True(result.Deleted);
        Assert.False(StudentExists(OldStudentId));
        Assert.Equal(0, Count(db => db.StudentLessonCompletions.Where(c => c.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.Certificates));
        Assert.Equal(0, Count(db => db.StudentRankProgresses.Where(p => p.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.StudentBadges.Where(b => b.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.AttendanceMarks.Where(m => m.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.ConsentRecords.Where(c => c.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.ConsentSensitiveDetails));
        Assert.Equal(0, Count(db => db.StudentGuardianLinks.Where(l => l.StudentId == OldStudentId)));
        Assert.Equal(0, Count(db => db.GuardianInvites));
        Assert.False(_blobs.TryGetBlob("certificates/100/300.pdf", out _));

        // The child's own sign-in account and its Identity row.
        Assert.Contains(ChildAccountId, result.AccountsDeleted);
        Assert.Equal(0, Count(db => db.AppUsers.Where(u => u.Id == ChildAccountId)));
        Assert.Equal(0, Count(db => db.Users.Where(u => u.Id == ChildAccountId)));

        // The chapter's register (a chapter record) and the other student stay.
        Assert.Equal(1, Count(db => db.AttendanceRegisters));
        Assert.True(StudentExists(ActiveStudentId));
    }

    [Fact]
    public async Task DeletingAStudent_DoesNotReadTheirConsentDetails()
    {
        // The details go by the database's cascade, so no read-audit row is written for them.
        using var ctx = CreateContext(Admin());

        await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Equal(0, Count(db => db.SensitiveDataAccessLogs.Where(l => l.Entity == nameof(ConsentSensitiveDetails))));
    }

    [Fact]
    public async Task DeletingAStudent_KeepsTheirApplication_UnderItsOwnRule()
    {
        AddApplication(700, OldStudentId, decidedAt: _now.AddMonths(-6));
        using var ctx = CreateContext(Admin());

        await Service(ctx).DeleteAsync(StudentRequest());

        using var check = CreateContext(SystemAdmin());
        var application = check.Context.StudentApplications.Single(a => a.Id == 700);
        Assert.Null(application.StudentId);
    }

    // ---- parent accounts -----------------------------------------------------------------------

    [Fact]
    public async Task AParent_IsDeletedWithTheirLastChild()
    {
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsDeleted);
        Assert.Equal(0, Count(db => db.ParentGuardians));
        Assert.Equal(0, Count(db => db.AppUsers.Where(u => u.Id == ParentAccountId)));
        Assert.Equal(0, Count(db => db.Users.Where(u => u.Id == ParentAccountId)));
        Assert.Equal(0, Count(db => db.UserRoles.Where(r => r.UserId == ParentAccountId)));
    }

    [Fact]
    public async Task AParent_WithAnotherChildInTheProgramme_IsKept()
    {
        LinkGuardianTo(ActiveStudentId);
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.DoesNotContain(ParentAccountId, result.AccountsDeleted);
        Assert.Equal(1, Count(db => db.ParentGuardians.Where(g => g.Id == GuardianId)));
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == ParentAccountId)));
    }

    [Fact]
    public async Task AParent_WhoIsCurrentStaff_IsKept()
    {
        AddRole(ParentAccountId, Role.Teacher, Shrewsbury);
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == ParentAccountId)));
        // The guardian row itself has no child left and goes.
        Assert.Equal(0, Count(db => db.ParentGuardians));
    }

    [Fact]
    public async Task AParent_WithARoleInAnotherChapter_IsKept()
    {
        AddRole(ParentAccountId, Role.Parent, Coventry);
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == ParentAccountId)));
    }

    [Fact]
    public async Task AParent_WithAnApplicationStillOnRecord_IsKept()
    {
        AddApplication(701, null, decidedAt: _now.AddMonths(-2));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsKept);
    }

    [Fact]
    public async Task AParent_WhoStoodDownFromStaffMoreThanSixYearsAgo_IsDeleted()
    {
        AddRole(ParentAccountId, Role.Teacher, Shrewsbury);
        MarkLeft(ParentAccountId, _now.AddYears(-7));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsDeleted);
    }

    [Fact]
    public async Task AParent_WhoStoodDownFromStaffRecently_IsKept()
    {
        AddRole(ParentAccountId, Role.Teacher, Shrewsbury);
        MarkLeft(ParentAccountId, _now.AddYears(-1));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(StudentRequest());

        Assert.Contains(ParentAccountId, result.AccountsKept);
    }

    // ---- DBS checks and staff accounts ---------------------------------------------------------

    [Fact]
    public async Task DeletingADbsCheck_RemovesIt_AndAStaffLeaversAccountWithNoChild()
    {
        MarkLeft(TeacherId, _now.AddYears(-7));
        RemoveTeacherHistory();
        AddDbsCheck(1, TeacherId, _now.AddYears(-4));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.True(result.Deleted);
        Assert.Contains(TeacherId, result.AccountsDeleted);
        Assert.Equal(0, Count(db => db.DbsChecks));
        Assert.Equal(0, Count(db => db.AppUsers.Where(u => u.Id == TeacherId)));
        Assert.Equal(0, Count(db => db.UserRoles.Where(r => r.UserId == TeacherId)));
    }

    [Fact]
    public async Task AStaffLeaver_WithAChildStillInTheProgramme_IsKept()
    {
        MarkLeft(TeacherId, _now.AddYears(-7));
        RemoveTeacherHistory();
        AddDbsCheck(1, TeacherId, _now.AddYears(-4));
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            seed.Context.ParentGuardians.Add(new ParentGuardian { Id = 201, Name = "Teacher Ten", ContactInfo = "t@example.com", IsPrimary = true, AppUserId = TeacherId });
            seed.Context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = ActiveStudentId, ParentGuardianId = 201, CreatedByUserId = AdminId });
            seed.Context.SaveChanges();
        }

        using var ctx = CreateContext(Admin());
        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.True(result.Deleted);
        Assert.Contains(TeacherId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == TeacherId)));
    }

    [Fact]
    public async Task AStaffLeaver_WithAnotherDbsCheckOnRecord_IsKept()
    {
        MarkLeft(TeacherId, _now.AddYears(-7));
        RemoveTeacherHistory();
        AddDbsCheck(1, TeacherId, _now.AddYears(-8));
        AddDbsCheck(2, TeacherId, _now.AddYears(-4));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.Contains(TeacherId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.DbsChecks));
    }

    [Fact]
    public async Task ACurrentTeachersExpiredDbsCheck_IsDeleted_ButTheirAccountIsKept()
    {
        AddDbsCheck(1, TeacherId, _now.AddYears(-7));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.True(result.Deleted);
        Assert.Contains(TeacherId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == TeacherId)));
    }

    [Fact]
    public async Task AStaffLeaver_NamedByAKeptRecord_IsKept()
    {
        // Teacher Ten took a register that the chapter keeps: deleting the account would break it.
        MarkLeft(TeacherId, _now.AddYears(-7));
        AddDbsCheck(1, TeacherId, _now.AddYears(-4));
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            seed.Context.StudentLessonCompletions.RemoveRange(seed.Context.StudentLessonCompletions);
            seed.Context.SaveChanges();
        }

        using var ctx = CreateContext(Admin());
        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.True(result.Deleted);
        Assert.Contains(TeacherId, result.AccountsKept);
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == TeacherId)));
    }

    [Fact]
    public async Task DeletingADbsCheck_ReadsItThroughTheAuditedPath()
    {
        AddDbsCheck(1, TeacherId, _now.AddYears(-7));
        using var ctx = CreateContext(Admin());

        await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        Assert.True(Count(db => db.SensitiveDataAccessLogs.Where(l => l.Entity == nameof(DbsCheck) && l.EntityId == 1 && l.UserId == AdminId)) >= 1);
    }

    // ---- applications --------------------------------------------------------------------------

    [Fact]
    public async Task DeletingAnApplication_RemovesItsRestrictedDetails()
    {
        AddApplication(702, null, decidedAt: _now.AddYears(-2));
        using var ctx = CreateContext(Admin());

        var result = await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.StudentApplication, 702, "Child 702"));

        Assert.True(result.Deleted);
        Assert.Equal(0, Count(db => db.StudentApplications));
        Assert.Equal(0, Count(db => db.StudentApplicationSensitiveDetails));
        // An application's deletion removes no account.
        Assert.Equal(1, Count(db => db.AppUsers.Where(u => u.Id == ParentAccountId)));
    }

    // ---- delete all shown ----------------------------------------------------------------------

    [Fact]
    public async Task DeleteAll_DeletesEachConfirmedRecord_AndReportsTheRefusedOnes()
    {
        AddApplication(702, null, decidedAt: _now.AddYears(-2));
        AddDbsCheck(1, TeacherId, _now.AddYears(-7));
        using var ctx = CreateContext(Admin());

        var results = await Service(ctx).DeleteAllAsync(
        [
            StudentRequest(),
            new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "wrong name"),
            new RetentionDeletionRequest(RetentionRecordKind.StudentApplication, 702, "Child 702")
        ]);

        // Applications first, so the application does not keep the parent's account.
        Assert.Equal([RetentionRecordKind.StudentApplication, RetentionRecordKind.DbsCheck, RetentionRecordKind.Student], results.Select(r => r.Kind));
        Assert.True(results[0].Deleted);
        Assert.False(results[1].Deleted);
        Assert.NotNull(results[1].Error);
        Assert.True(results[2].Deleted);
        Assert.Contains(ParentAccountId, results[2].AccountsDeleted);
        Assert.Equal(1, Count(db => db.DbsChecks));
    }

    // ---- the deletion log ----------------------------------------------------------------------

    [Fact]
    public async Task EachDeletion_WritesALogRow_WithWhoAndWhen()
    {
        using var ctx = CreateContext(Admin());
        var before = DateTime.UtcNow.AddSeconds(-1);

        await Service(ctx).DeleteAsync(StudentRequest());

        using var check = CreateContext(SystemAdmin());
        var rows = check.Context.DeletionLog.OrderBy(r => r.Id).ToList();
        Assert.Equal(
            [
                (DeletionLogEntry.RecordTypes.Student, OldStudentId),
                (DeletionLogEntry.RecordTypes.ParentGuardian, GuardianId),
                (DeletionLogEntry.RecordTypes.Account, ChildAccountId),
                (DeletionLogEntry.RecordTypes.Account, ParentAccountId)
            ],
            rows.Select(r => (r.RecordType, r.RecordId)));
        Assert.All(rows, r =>
        {
            Assert.Equal(AdminId, r.DeletedByUserId);
            Assert.Equal(Shrewsbury, r.ChapterId);
            Assert.True(r.DeletedAt >= before);
        });
    }

    [Fact]
    public void TheDeletionLog_HoldsNoPersonalData()
    {
        // Only these columns, ever: a new column must be reviewed for personal data first.
        var columns = typeof(DeletionLogEntry).GetProperties().Select(p => p.Name).Order().ToList();
        Assert.Equal(["ChapterId", "DeletedAt", "DeletedByUserId", "Id", "RecordId", "RecordType"], columns);
    }

    [Fact]
    public async Task TheDeletionLog_CarriesNoNames()
    {
        AddDbsCheck(1, TeacherId, _now.AddYears(-7));
        using var ctx = CreateContext(Admin());

        await Service(ctx).DeleteAsync(StudentRequest());
        await Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.DbsCheck, 1, "Teacher Ten"));

        using var check = CreateContext(SystemAdmin());
        var types = check.Context.DeletionLog.Select(r => r.RecordType).ToList();
        Assert.Equal(5, types.Count);
        Assert.All(types, t => Assert.Contains(t, new[]
        {
            DeletionLogEntry.RecordTypes.Student, DeletionLogEntry.RecordTypes.DbsCheck, DeletionLogEntry.RecordTypes.StudentApplication,
            DeletionLogEntry.RecordTypes.ParentGuardian, DeletionLogEntry.RecordTypes.Account
        }));
        foreach (var name in new[] { "Alex", "Pat", "Teacher Ten", "CERT-1", "07000" })
        {
            Assert.DoesNotContain(types, t => t.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ARefusedDeletion_WritesNoLogRow()
    {
        using var ctx = CreateContext(Admin());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(ctx).DeleteAsync(new RetentionDeletionRequest(RetentionRecordKind.Student, ActiveStudentId, "Casey Current")));

        Assert.Equal(0, Count(db => db.DeletionLog));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static FakeCurrentUserContext Admin() => new(AdminId, Role.Admin);

    private static FakeCurrentUserContext SystemAdmin() => new(null, Role.Admin) { SeesAllChapters = true };

    private static RetentionDeletionRequest StudentRequest() => new(RetentionRecordKind.Student, OldStudentId, "Alex Old");

    private RetentionDeletionService Service(CallerContext ctx) =>
        new(ctx.Context, ctx.CurrentUser, _blobs, NullLogger<RetentionDeletionService>.Instance);

    private bool StudentExists(int id) => Count(db => db.Students.Where(s => s.Id == id)) == 1;

    private int Count<T>(Func<VlmsDbContext, IQueryable<T>> query)
    {
        using var check = CreateContext(SystemAdmin());
        return query(check.Context).Count();
    }

    private static Student NewStudent(int id, string name, StudentStatus status, DateTime? leftAt, int? appUserId, int chapterId = 0) => new()
    {
        ChapterId = chapterId,
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2010, 1, 1),
        CurrentRankId = 1,
        Status = status,
        EnrolmentDate = new DateOnly(2018, 1, 1),
        LeftAt = leftAt,
        AppUserId = appUserId
    };

    private static ConsentRecord NewConsent(int id, int studentId, int guardianId) => new()
    {
        Id = id,
        StudentId = studentId,
        PeriodStart = new DateOnly(2020, 9, 1),
        PeriodEnd = new DateOnly(2021, 8, 31),
        PhotoMediaConsent = true,
        TransportConsent = true,
        OffsiteTripConsent = true,
        DataSharingConsent = true,
        Status = ConsentStatus.Approved,
        SubmittedByParentId = guardianId,
        ExpiryDate = new DateOnly(2021, 8, 31)
    };

    private static void AddUser(VlmsDbContext db, int id, string name, params (Role Role, int? ChapterId)[] roles)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        db.Users.Add(new VlmsIdentityUser { Id = id, UserName = $"user{id}", Email = $"user{id}@example.com" });
        foreach (var (role, chapterId) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapterId });
        }
    }

    private void AddRole(int userId, Role role, int chapterId)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        seed.Context.UserRoles.Add(new UserRole { UserId = userId, Role = role, ChapterId = chapterId });
        seed.Context.SaveChanges();
    }

    private void MarkLeft(int userId, DateTime leftAt)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        var user = seed.Context.AppUsers.Single(u => u.Id == userId);
        seed.Context.Entry(user).Property(u => u.HasLeft).CurrentValue = true;
        seed.Context.Entry(user).Property(u => u.LeftAt).CurrentValue = leftAt;
        seed.Context.SaveChanges();
    }

    /// <summary>Removes the records that name Teacher Ten (the completion and the register), as if long since deleted.</summary>
    private void RemoveTeacherHistory()
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        seed.Context.StudentLessonCompletions.RemoveRange(seed.Context.StudentLessonCompletions);
        seed.Context.AttendanceRegisters.RemoveRange(seed.Context.AttendanceRegisters);
        seed.Context.SaveChanges();
    }

    private void LinkGuardianTo(int studentId)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        seed.Context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = studentId, ParentGuardianId = GuardianId, CreatedByUserId = AdminId });
        seed.Context.SaveChanges();
    }

    private void AddDbsCheck(int id, int teacherId, DateTime expiry)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        seed.Context.DbsChecks.Add(new DbsCheck
        {
            Id = id, TeacherUserId = teacherId, CheckDate = DateOnly.FromDateTime(expiry.AddYears(-3)),
            ExpiryDate = DateOnly.FromDateTime(expiry), CertificateNumber = $"CERT-{id}", Status = DbsCheckStatus.Clear
        });
        seed.Context.SaveChanges();
    }

    private void AddApplication(int id, int? studentId, DateTime decidedAt)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        seed.Context.StudentApplications.Add(new StudentApplication
        {
            ChapterId = Shrewsbury,
            Id = id,
            ApplicantUserId = ParentAccountId,
            ParentPhone = "07000 000000",
            ParentPostalAddress = "1 Street",
            Relationship = "Mother",
            ChildName = $"Child {id}",
            ChildDateOfBirth = new DateOnly(2015, 1, 1),
            PhotoMediaConsent = true,
            TransportConsent = true,
            OffsiteTripConsent = true,
            DataSharingConsent = true,
            Status = StudentApplicationStatus.Approved,
            SubmittedAt = decidedAt.AddDays(-10),
            LastSubmittedAt = decidedAt.AddDays(-10),
            AdminDecidedByUserId = AdminId,
            AdminDecidedAt = decidedAt,
            StudentId = studentId
        });
        seed.Context.StudentApplicationSensitiveDetails.Add(new StudentApplicationSensitiveDetails
        {
            ChapterId = Shrewsbury, Id = id, StudentApplicationId = id, EmergencyContact = "Someone"
        });
        seed.Context.SaveChanges();
    }

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ICurrentUserContext CurrentUser { get; } = currentUser;

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(ICurrentUserContext currentUser)
    {
        var services = new ServiceCollection();
        services.AddSingleton(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new CallerContext(provider, scope, scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), currentUser);
    }
}
