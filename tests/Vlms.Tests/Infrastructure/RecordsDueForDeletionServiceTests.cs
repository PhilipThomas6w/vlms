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
/// the documented judgement calls this test suite exercises: only Graduated students can be
/// flagged (no "leaving date" exists anywhere for Inactive students), and only DBS expiry (not "the
/// teacher leaving", which has no schema support either).
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

        using var schema = CreateContext(SystemCurrentUserContext.Instance);
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

    private static Student NewStudent(int id, string name, StudentStatus status, DateOnly enrolmentDate) => new()
    {
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2012, 1, 1),
        CurrentRankId = 1,
        Status = status,
        EnrolmentDate = enrolmentDate
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
    /// real closed <see cref="StudentRankProgress"/> row (not a fabricated date field, since none
    /// exists); a Graduated student who graduated 1 year ago (within retention); a Graduated student
    /// with no closed <see cref="StudentRankProgress"/> row at all (data anomaly — must not throw,
    /// must not be flagged); an Active student and an Inactive student (both excluded — Inactive
    /// specifically because no leaving date is ever recorded, see <see cref="RetentionFlagging"/>'s
    /// doc comment). Also: one teacher whose only DBS check expired 7 years ago (past the 6-year
    /// retention period), one whose only check expired 5 years ago (within retention), and one with
    /// two checks — one past retention, one not — proving each row is flagged independently rather
    /// than only the teacher's latest.
    /// </summary>
    private void SeedScenario(DateTime now)
    {
        using var seed = CreateContext(SystemCurrentUserContext.Instance);
        var db = seed.Context;

        db.Students.Add(NewStudent(100, "Graduated Long Ago", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-6))));
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 1, StudentId = 100, RankId = 1, StartedAt = now.AddYears(-6), CompletedAt = now.AddYears(-4) });

        db.Students.Add(NewStudent(101, "Recently Graduated", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-2))));
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 2, StudentId = 101, RankId = 1, StartedAt = now.AddYears(-2), CompletedAt = now.AddYears(-1) });

        db.Students.Add(NewStudent(102, "Graduated No History Row", StudentStatus.Graduated, DateOnly.FromDateTime(now.AddYears(-5))));

        db.Students.Add(NewStudent(103, "Still Active", StudentStatus.Active, DateOnly.FromDateTime(now.AddYears(-1))));
        db.Students.Add(NewStudent(104, "Inactive", StudentStatus.Inactive, DateOnly.FromDateTime(now.AddYears(-6))));

        db.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher Past Retention", Email = "t10@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(1, 10, DateOnly.FromDateTime(now.AddYears(-7))));

        db.AppUsers.Add(new AppUser { Id = 11, DisplayName = "Teacher Within Retention", Email = "t11@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 11, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(2, 11, DateOnly.FromDateTime(now.AddYears(-5))));

        db.AppUsers.Add(new AppUser { Id = 12, DisplayName = "Teacher Two Checks", Email = "t12@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 12, Role = Role.Teacher });
        db.DbsChecks.Add(NewDbsCheck(3, 12, DateOnly.FromDateTime(now.AddYears(-8)), DbsCheckStatus.Flagged));
        db.DbsChecks.Add(NewDbsCheck(4, 12, DateOnly.FromDateTime(now.AddYears(-1))));

        db.SaveChanges();
    }

    [Fact]
    public async Task GetReportAsync_ByAdmin_FlagsOnlyGraduatedStudentsAndDbsChecksPastRetention()
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);

        var report = await run.Service(admin).GetReportAsync();

        var flaggedStudent = Assert.Single(report.Students);
        Assert.Equal(100, flaggedStudent.StudentId);
        Assert.Equal("Graduated Long Ago", flaggedStudent.StudentName);
        Assert.True(flaggedStudent.DaysPastRetention > 0);

        Assert.DoesNotContain(report.Students, s => s.StudentId == 101); // graduated within retention
        Assert.DoesNotContain(report.Students, s => s.StudentId == 102); // no history row — data anomaly, not fabricated
        Assert.DoesNotContain(report.Students, s => s.StudentId == 103); // Active
        Assert.DoesNotContain(report.Students, s => s.StudentId == 104); // Inactive — no leaving date exists

        Assert.Equal(2, report.DbsChecks.Count);
        Assert.Contains(report.DbsChecks, d => d.DbsCheckId == 1 && d.TeacherUserId == 10);
        Assert.Contains(report.DbsChecks, d => d.DbsCheckId == 3 && d.TeacherUserId == 12);
        Assert.DoesNotContain(report.DbsChecks, d => d.DbsCheckId == 2); // within retention
        Assert.DoesNotContain(report.DbsChecks, d => d.DbsCheckId == 4); // this teacher's other, non-expired check
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
    /// <see cref="SensitiveDataAccessLog"/> row is written. Asserts one audit row per flagged
    /// <see cref="DbsCheck"/> row this report reads, proving
    /// <see cref="RetentionFlagging.GetDbsChecksDueForDeletionAsync"/> materializes real entities.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_ReadingDbsChecks_WritesOneSensitiveDataAccessLogEntryPerRowRead()
    {
        var now = DateTime.UtcNow;
        SeedScenario(now);

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);

        await run.Service(admin).GetReportAsync();

        using var verify = CreateContext(SystemCurrentUserContext.Instance);
        var logEntries = verify.Context.SensitiveDataAccessLogs
            .Where(l => l.Entity == nameof(DbsCheck))
            .OrderBy(l => l.EntityId)
            .ToList();

        Assert.Equal(2, logEntries.Count);
        Assert.Equal(1, logEntries[0].EntityId);
        Assert.Equal(3, logEntries[1].EntityId);
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
            report.Students.Select(s => (s.StudentId, s.GraduatedAt)).OrderBy(x => x.StudentId),
            jobResult.StudentsDueForDeletion.Select(s => (s.StudentId, s.GraduatedAt)).OrderBy(x => x.StudentId));

        Assert.Equal(
            report.DbsChecks.Select(d => (d.DbsCheckId, d.ExpiryDate)).OrderBy(x => x.DbsCheckId),
            jobResult.DbsChecksDueForDeletion.Select(d => (d.DbsCheckId, d.ExpiryDate)).OrderBy(x => x.DbsCheckId));
    }
}
