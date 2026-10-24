using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="RecordsDueForDeletionService"/> (STATE.md "'Records due for deletion'
/// report", data-design.md "Deletion enforcement — interim decision",
/// security-compliance.md "Retention"). See <see cref="RetentionFlagging"/>'s own doc comment for
/// the documented judgement calls this test suite exercises: Graduated students (anchor derived from
/// <see cref="StudentRankProgress.CompletedAt"/>) and Inactive students with a recorded
/// <see cref="Student.LeftAt"/> (STATE.md "Manual 'leaving' flag..." item, set by
/// <see cref="Retention.LeavingService"/>) can both be flagged; DBS checks past their own expiry, or
/// whose teacher has left earlier than that expiry (<see cref="AppUser.HasLeft"/>/
/// <see cref="AppUser.LeftAt"/>), can both be flagged too.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="ProgressReportingServiceTests"/>/
/// <see cref="ConsentExpiryJobTests"/> — this service legitimately reads <see cref="DbsCheck"/> (a
/// whole-entity-restricted, read-audited entity), so the same real-SQLite/DI-resolved-context
/// requirements apply.
/// </summary>
public sealed class RecordsDueForDeletionServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public RecordsDueForDeletionServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-duefordeletion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(TestSeedUserContext.Instance);
        schema.Context.Database.EnsureCreated();
        schema.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public RecordsDueForDeletionService Service(ICurrentUserContext currentUser) => new(Context, currentUser);

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
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context);
    }

    private static Student NewStudent(int id, string name, StudentStatus status, DateOnly enrolmentDate, DateTime? leftAt = null) => new()
    {
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2012, 1, 1),
        CurrentRankId = 1,
        Status = status,
        EnrolmentDate = enrolmentDate,
        LeftAt = leftAt
    };

    private static AppUser NewTeacher(int id, string name, bool hasLeft = false, DateTime? leftAt = null) => new()
    {
        Id = id,
        DisplayName = name,
        Email = $"t{id}@example.com",
        HasLeft = hasLeft,
        LeftAt = leftAt
    };

    private static DbsCheck NewDbsCheck(int id, int teacherUserId, DateOnly expiryDate, DbsCheckStatus status = DbsCheckStatus.Clear) => new()
    {
        Id = id,
        TeacherUserId = teacherUserId,
        CheckDate = expiryDate.AddYears(-3),
        ExpiryDate = expiryDate,
        CertificateNumber = $"CERT-{id}",
        Status = status
    };

    /// <summary>
    /// Seeds: a Graduated student who graduated 4 years ago (past the 3-year retention period) via a
    /// real closed <see cref="StudentRankProgress"/> row; a Graduated student who graduated 1 year ago
    /// (within retention); a Graduated student with no closed <see cref="StudentRankProgress"/> row at
    /// all (data anomaly — must not throw, must not be flagged); an Active student; an Inactive
    /// student with no <see cref="Student.LeftAt"/> recorded (anomaly, must not be flagged); an
    /// Inactive student who left 4 years ago (past retention — <see cref="StudentDeletionReason.Left"/>);
    /// an Inactive student who left 1 year ago (within retention). Also: one teacher whose only DBS
    /// check expired 7 years ago (past the 6-year retention period), one whose only check expired 5
    /// years ago (within retention), one with two checks — one past retention, one not — proving each
    /// row is flagged independently rather than only the teacher's latest; and two teachers who have
    /// left — one 7 years ago (whose check's own expiry is only 3 years ago, so only the teacher's
    /// leaving brings it due — <see cref="DbsCheckDeletionReason.TeacherLeft"/>), one 2 years ago
    /// (whose check expired 1 year ago — within retention either way).
    /// </summary>
    private void SeedScenario(DateTime now)
    {
        using var seed = CreateContext(TestSeedUserContext.Instance);
        var db = seed.Context;

        db.Students.Add(NewStudent(100, "Graduated Long Ago", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-6))));
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 1, StudentId = 100, RankId = 1, StartedAt = now.AddYears(-6), CompletedAt = now.AddYears(-4) });

        db.Students.Add(NewStudent(101, "Recently Graduated", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-2))));
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 2, StudentId = 101, RankId = 1, StartedAt = now.AddYears(-2), CompletedAt = now.AddYears(-1) });

        db.Students.Add(NewStudent(102, "Graduated No History Row", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-5))));

        db.Students.Add(NewStudent(103, "Still Active", StudentStatus.Active, DateOnly.FromDateTime(now.AddYears(-1))));
        db.Students.Add(NewStudent(104, "Inactive No LeftAt", StudentStatus.Inactive, DateOnly.FromDateTime(now.AddYears(-6))));
        db.Students.Add(NewStudent(105, "Left Long Ago", StudentStatus.Inactive, DateOnly.FromDateTime(now.AddYears(-10)), leftAt: now.AddYears(-4)));
        db.Students.Add(NewStudent(106, "Recently Left", StudentStatus.Inactive, DateOnly.FromDateTime(now.AddYears(-10)), leftAt: now.AddYears(-1)));

        db.AppUsers.Add(NewTeacher(10, "Teacher Past Retention"));
        db.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(1, 10, DateOnly.FromDateTime(now.AddYears(-7))));

        db.AppUsers.Add(NewTeacher(11, "Teacher Within Retention"));
        db.UserRoles.Add(new UserRole { UserId = 11, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(2, 11, DateOnly.FromDateTime(now.AddYears(-5))));

        db.AppUsers.Add(NewTeacher(12, "Teacher Two Checks"));
        db.UserRoles.Add(new UserRole { UserId = 12, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(3, 12, DateOnly.FromDateTime(now.AddYears(-8)), DbsCheckStatus.Flagged));
        db.DbsChecks.Add(NewDbsCheck(4, 12, DateOnly.FromDateTime(now.AddYears(-1))));

        // ExpiryDate (-3y) is comfortably within its own 6-year window — only this teacher's leaving
        // (-7y) brings it due.
        db.AppUsers.Add(NewTeacher(13, "Teacher Left Long Ago", hasLeft: true, leftAt: now.AddYears(-7)));
        db.UserRoles.Add(new UserRole { UserId = 13, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(5, 13, DateOnly.FromDateTime(now.AddYears(-3))));

        db.AppUsers.Add(NewTeacher(14, "Teacher Recently Left", hasLeft: true, leftAt: now.AddYears(-2)));
        db.UserRoles.Add(new UserRole { UserId = 14, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(6, 14, DateOnly.FromDateTime(now.AddYears(-1))));

        db.SaveChanges();
    }

    [Fact]
    public async Task GetReportAsync_ByAdmin_FlagsGraduatedAndLeftStudentsAndDbsChecksPastRetention()
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);

        var report = await run.Service(admin).GetReportAsync();

        Assert.Equal(2, report.Students.Count);

        var graduatedFlag = Assert.Single(report.Students, s => s.StudentId == 100);
        Assert.Equal("Graduated Long Ago", graduatedFlag.StudentName);
        Assert.Equal(StudentDeletionReason.Graduated, graduatedFlag.Reason);
        Assert.True(graduatedFlag.DaysPastRetention > 0);

        var leftFlag = Assert.Single(report.Students, s => s.StudentId == 105);
        Assert.Equal("Left Long Ago", leftFlag.StudentName);
        Assert.Equal(StudentDeletionReason.Left, leftFlag.Reason);
        Assert.True(leftFlag.DaysPastRetention > 0);

        Assert.DoesNotContain(report.Students, s => s.StudentId == 101); // graduated within retention
        Assert.DoesNotContain(report.Students, s => s.StudentId == 102); // no history row — data anomaly, not fabricated
        Assert.DoesNotContain(report.Students, s => s.StudentId == 103); // Active
        Assert.DoesNotContain(report.Students, s => s.StudentId == 104); // Inactive, no LeftAt — data anomaly, not fabricated
        Assert.DoesNotContain(report.Students, s => s.StudentId == 106); // left within retention

        Assert.Equal(3, report.DbsChecks.Count);

        var expiredFlag = Assert.Single(report.DbsChecks, d => d.DbsCheckId == 1);
        Assert.Equal(10, expiredFlag.TeacherUserId);
        Assert.Equal(DbsCheckDeletionReason.Expired, expiredFlag.Reason);

        Assert.Contains(report.DbsChecks, d => d.DbsCheckId == 3 && d.TeacherUserId == 12 && d.Reason == DbsCheckDeletionReason.Expired);

        var teacherLeftFlag = Assert.Single(report.DbsChecks, d => d.DbsCheckId == 5);
        Assert.Equal(13, teacherLeftFlag.TeacherUserId);
        Assert.Equal(DbsCheckDeletionReason.TeacherLeft, teacherLeftFlag.Reason);
        Assert.Equal(now.AddYears(-7).Date, teacherLeftFlag.AnchorDate.ToDateTime(TimeOnly.MinValue).Date);

        Assert.DoesNotContain(report.DbsChecks, d => d.DbsCheckId == 2); // within retention
        Assert.DoesNotContain(report.DbsChecks, d => d.DbsCheckId == 4); // this teacher's other, non-expired check
        Assert.DoesNotContain(report.DbsChecks, d => d.DbsCheckId == 6); // teacher left within retention, check itself within retention
    }

    private static StudentApplication NewApplication(int id, string child, StudentApplicationStatus status, DateTime submittedAt,
        DateTime? safeguardingReviewedAt = null, DateTime? adminDecidedAt = null) => new()
    {
        Id = id,
        ApplicantUserId = 50,
        ChapterId = Chapter.ShrewsburyId,
        ParentPhone = "07000 000000",
        ParentPostalAddress = "1 Church Lane",
        Relationship = "Mother",
        ChildName = child,
        ChildDateOfBirth = new DateOnly(2015, 1, 1),
        PhotoMediaConsent = false,
        TransportConsent = false,
        OffsiteTripConsent = false,
        DataSharingConsent = false,
        Status = status,
        SubmittedAt = submittedAt,
        LastSubmittedAt = submittedAt,
        SafeguardingReviewedAt = safeguardingReviewedAt,
        AdminDecidedAt = adminDecidedAt
    };

    /// <summary>
    /// Registration applications are kept 1 year after the decision (Philip, 23/10/2026): the Admin's
    /// final decision, or the Safeguarding Officer's stage-1 rejection. Applications still under
    /// review are never flagged, however old.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_ByAdmin_FlagsApplicationsDecidedMoreThanOneYearAgo()
    {
        var now = DateTime.UtcNow;
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            seed.Context.AppUsers.Add(new AppUser { Id = 50, DisplayName = "Applicant", Email = "applicant@example.com" });
            seed.Context.StudentApplications.AddRange(
                NewApplication(1, "Approved Long Ago", StudentApplicationStatus.Approved, now.AddMonths(-15), now.AddMonths(-14), now.AddMonths(-13)),
                NewApplication(2, "Rejected At Stage One Long Ago", StudentApplicationStatus.Rejected, now.AddMonths(-15), now.AddMonths(-14)),
                NewApplication(3, "Rejected By Admin Recently", StudentApplicationStatus.Rejected, now.AddMonths(-15), now.AddMonths(-14), now.AddMonths(-11)),
                NewApplication(4, "Approved Recently", StudentApplicationStatus.Approved, now.AddMonths(-3), now.AddMonths(-2), now.AddMonths(-1)),
                NewApplication(5, "Still Waiting For Review", StudentApplicationStatus.AwaitingSafeguardingReview, now.AddYears(-2)),
                NewApplication(6, "Still Waiting For Admin", StudentApplicationStatus.AwaitingAdminApproval, now.AddYears(-2), now.AddYears(-2)));
            seed.Context.SaveChanges();
        }

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var report = await run.Service(new FakeCurrentUserContext(userId: 1, Role.Admin)).GetReportAsync();

        Assert.Equal([1, 2], report.Applications.Select(a => a.ApplicationId).Order());
        var approved = Assert.Single(report.Applications, a => a.ApplicationId == 1);
        Assert.Equal(StudentApplicationStatus.Approved, approved.Status);
        Assert.Equal(now.AddMonths(-13), approved.DecidedAt, TimeSpan.FromSeconds(1));
        Assert.True(approved.DaysPastRetention > 0);
        var rejected = Assert.Single(report.Applications, a => a.ApplicationId == 2);
        Assert.Equal(now.AddMonths(-14), rejected.DecidedAt, TimeSpan.FromSeconds(1));
        Assert.Empty(run.Context.SensitiveDataAccessLogs);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task GetReportAsync_ByNonAdminRole_Throws(Role role)
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var caller = new FakeCurrentUserContext(userId: 1, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetReportAsync());
    }

    /// <summary>
    /// Regression guard for the exact bug class a prior checker review found in
    /// <see cref="ConsentExpiryJob.SweepDbsAsync"/> (commit d23f6e4): a projection-based
    /// <see cref="DbsCheck"/> read never triggers EF Core's <c>IMaterializationInterceptor</c>, so no
    /// <see cref="SensitiveDataAccessLog"/> row is written. <see cref="RetentionFlagging.GetDbsChecksDueForDeletionAsync"/>
    /// now materializes every <see cref="DbsCheck"/> row (not just the ones ultimately flagged — see
    /// its own doc comment for why: evaluating "due" requires per-row teacher data a SQL-side
    /// <c>ExpiryDate</c>-only filter can't express), so this asserts one audit row per <em>seeded</em>
    /// <see cref="DbsCheck"/> row, not just per flagged row.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_ReadingDbsChecks_WritesOneSensitiveDataAccessLogEntryPerRowRead()
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);

        await run.Service(admin).GetReportAsync();

        using var verify = CreateContext(TestSeedUserContext.Instance);
        var logEntries = verify.Context.SensitiveDataAccessLogs
            .Where(l => l.Entity == nameof(DbsCheck))
            .OrderBy(l => l.EntityId)
            .ToList();

        Assert.Equal(6, logEntries.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], logEntries.Select(e => e.EntityId));
        Assert.All(logEntries, e => Assert.Equal(SensitiveAccessType.View, e.AccessType));
    }

    /// <summary>
    /// The property that actually matters for the DRY extraction (STATE.md's own instruction to
    /// check <see cref="AtRiskStudentFlagging"/>'s precedent first): seeds the identical scenario and
    /// asserts <see cref="ConsentExpiryJob"/>'s daily sweep and this on-demand Admin report produce
    /// the exact same flagged population from the shared <see cref="RetentionFlagging"/> helper.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_ProducesTheSamePopulation_AsConsentExpiryJobsSweep()
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var reportRun = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);
        var report = await reportRun.Service(admin).GetReportAsync();

        using var jobRun = CreateContext(SystemCurrentUserContext.Instance);
        var jobResult = await new ConsentExpiryJob(jobRun.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>())
            .RunAsync();

        Assert.Equal(
            report.Students.Select(s => (s.StudentId, s.Reason, s.AnchorDate)).OrderBy(x => x.StudentId),
            jobResult.StudentsDueForDeletion.Select(s => (s.StudentId, s.Reason, s.AnchorDate)).OrderBy(x => x.StudentId));

        Assert.Equal(
            report.DbsChecks.Select(d => (d.DbsCheckId, d.Reason, d.AnchorDate)).OrderBy(x => x.DbsCheckId),
            jobResult.DbsChecksDueForDeletion.Select(d => (d.DbsCheckId, d.Reason, d.AnchorDate)).OrderBy(x => x.DbsCheckId));
    }
}
