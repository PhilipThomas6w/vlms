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
/// Verifies <see cref="ProgressReportingService"/> (STATE.md "Reporting screens: core progress
/// stats + at-risk flagging", functional.md "Reporting (MVP)": "Core progress reports:
/// rank/completion stats, promotion history" and "At-risk/disengaged student flagging").
///
/// Two things under test:
/// 1. <see cref="ProgressReportingService.GetProgressStatsAsync"/> — a realistic multi-student,
///    multi-rank scenario, asserting exact counts/ordering rather than just non-empty collections.
/// 2. <see cref="ProgressReportingService.GetAtRiskStudentsAsync"/> — proves it is not a duplicate
///    implementation of the disengagement window by seeding the exact same scenario
///    <see cref="ConsentExpiryJobTests"/> uses for its own at-risk test and asserting both
///    <see cref="ConsentExpiryJob"/> and this service produce the identical flagged population from
///    the shared <see cref="AtRiskStudentFlagging"/> helper.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="ParentDashboardServiceTests"/>.
/// </summary>
public sealed class ProgressReportingServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ProgressReportingServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-reporting-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ProgressReportingService Service(ICurrentUserContext currentUser) => new(Context, currentUser);

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

    private static Student NewStudent(int id, string name, StudentStatus status, int currentRankId, DateOnly enrolmentDate) => new()
    {
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2012, 1, 1),
        CurrentRankId = currentRankId,
        Status = status,
        EnrolmentDate = enrolmentDate
    };

    /// <summary>
    /// Seeds a multi-student, multi-rank scenario: 3 Ranks; Active students spread across two of
    /// them (one rank with zero active students, to prove zero-counts aren't just omitted); one
    /// Inactive and one Graduated student (excluded from the by-rank active counts, included in the
    /// status totals); 3 non-reversed completions plus 1 reversed one (excluded from the total); and
    /// two closed StudentRankProgress rows (promotion events) plus one still-open row (not a
    /// promotion event).
    /// </summary>
    private void SeedProgressStatsScenario()
    {
        using var seed = CreateContext(SystemCurrentUserContext.Instance);
        var db = seed.Context;
        db.Database.EnsureCreated();

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Explorer" });
        db.Ranks.Add(new Rank { Id = 3, Order = 3, Code = "R3", Name = "Pioneer" });

        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "b1", IsActive = true });
        db.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "t1@example.com" });

        // Active students: Alex + Bailey in Recruit (Rank 1), Casey in Explorer (Rank 2). Rank 3
        // (Pioneer) has zero active students despite existing as reference data.
        db.Students.Add(NewStudent(100, "Alex", StudentStatus.Active, 1, new DateOnly(2024, 1, 1)));
        db.Students.Add(NewStudent(101, "Bailey", StudentStatus.Active, 1, new DateOnly(2024, 1, 1)));
        db.Students.Add(NewStudent(102, "Casey", StudentStatus.Active, 2, new DateOnly(2024, 1, 1)));

        // Inactive/Graduated students are excluded from the by-rank active counts but counted in
        // the status totals.
        db.Students.Add(NewStudent(103, "Drew", StudentStatus.Inactive, 1, new DateOnly(2023, 1, 1)));
        db.Students.Add(NewStudent(104, "Evan", StudentStatus.Graduated, 3, new DateOnly(2022, 1, 1)));

        // 3 non-reversed completions + 1 reversed one (excluded from the total).
        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = 10, CompletedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = false });
        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 2, StudentId = 101, LessonId = 1, CompletedByUserId = 10, CompletedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = false });
        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 3, StudentId = 102, LessonId = 1, CompletedByUserId = 10, CompletedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = false });
        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 4, StudentId = 103, LessonId = 1, CompletedByUserId = 10, CompletedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = true });

        // Promotion history: Casey (102) closed their Recruit row on 1 Mar 2024 (promoted to
        // Explorer, where they now have an open row) — one promotion event. Evan (104) closed
        // their Pioneer row on 1 Jul 2024 (graduation is also a "completed the rank" event per
        // data-design.md) — a second promotion event, more recent than Casey's. Alex's Recruit row
        // is still open — not a promotion event.
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 1, StudentId = 100, RankId = 1, StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CompletedAt = null });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 2, StudentId = 102, RankId = 1, StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CompletedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 3, StudentId = 102, RankId = 2, StartedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CompletedAt = null });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = 4, StudentId = 104, RankId = 3, StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CompletedAt = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc) });

        db.SaveChanges();
    }

    [Fact]
    public async Task GetProgressStatsAsync_ByAdmin_ReturnsExactRankAndStatusCounts_CompletionTotal_AndOrderedPromotionHistory()
    {
        SeedProgressStatsScenario();

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);

        var report = await run.Service(admin).GetProgressStatsAsync();

        Assert.Collection(report.StudentsByRank,
            r => { Assert.Equal("Recruit", r.RankName); Assert.Equal(2, r.ActiveStudentCount); },
            r => { Assert.Equal("Explorer", r.RankName); Assert.Equal(1, r.ActiveStudentCount); },
            r => { Assert.Equal("Pioneer", r.RankName); Assert.Equal(0, r.ActiveStudentCount); });

        Assert.Equal(3, report.StatusCounts.ActiveCount);
        Assert.Equal(1, report.StatusCounts.InactiveCount);
        Assert.Equal(1, report.StatusCounts.GraduatedCount);

        Assert.Equal(3, report.TotalLessonCompletions);

        Assert.Collection(report.PromotionHistory,
            e => { Assert.Equal(104, e.StudentId); Assert.Equal("Evan", e.StudentName); Assert.Equal("Pioneer", e.RankName); Assert.Equal(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), e.CompletedAt); },
            e => { Assert.Equal(102, e.StudentId); Assert.Equal("Casey", e.StudentName); Assert.Equal("Recruit", e.RankName); Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), e.CompletedAt); });
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task GetProgressStatsAsync_ByNonAdminRole_Throws(Role role)
    {
        SeedProgressStatsScenario();

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var caller = new FakeCurrentUserContext(userId: 1, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetProgressStatsAsync());
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task GetAtRiskStudentsAsync_ByNonAdminRole_Throws(Role role)
    {
        SeedProgressStatsScenario();

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var caller = new FakeCurrentUserContext(userId: 1, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetAtRiskStudentsAsync());
    }

    /// <summary>
    /// The property that actually matters for the DRY extraction: seeds the identical disengagement
    /// scenario <see cref="ConsentExpiryJobTests.RunAsync_FlagsAtRiskStudents_ByCompletionGap_OrEnrolmentIfNever_ButGivesNewEnrolmentsAFairChance"/>
    /// uses, then asserts <see cref="ConsentExpiryJob"/>'s WebJob sweep and this on-demand Admin
    /// reporting screen produce the exact same flagged population (same StudentIds, same
    /// LastActivityAt, same DaysSinceLastActivity) — proving they call one shared implementation
    /// rather than two independently-maintained copies of the 8-week window.
    /// </summary>
    [Fact]
    public async Task GetAtRiskStudentsAsync_ProducesTheSamePopulation_AsConsentExpiryJobsSweep()
    {
        var now = DateTime.UtcNow;

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            var db = seed.Context;
            db.Database.EnsureCreated();

            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "b1", IsActive = true });
            db.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "t10", DisplayName = "Teacher One", Email = "t10@example.com" });

            // Disengaged: last completion 70 days ago (> 56-day threshold).
            db.Students.Add(NewStudent(200, "Disengaged", StudentStatus.Active, 1, DateOnly.FromDateTime(now.AddYears(-1))));
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 200, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-70), IsReversed = false });

            // Recently active: last completion 30 days ago (< threshold) — not at-risk.
            db.Students.Add(NewStudent(201, "Recently Active", StudentStatus.Active, 1, DateOnly.FromDateTime(now.AddYears(-1))));
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 2, StudentId = 201, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-30), IsReversed = false });

            // Enrolled long ago, never completed anything — at-risk from enrolment date.
            db.Students.Add(NewStudent(203, "Long-Enrolled Never Completed", StudentStatus.Active, 1, DateOnly.FromDateTime(now.AddDays(-100))));

            db.SaveChanges();
        }

        using var jobRun = CreateContext(SystemCurrentUserContext.Instance);
        var job = new ConsentExpiryJob(jobRun.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>());
        var jobResult = await job.RunAsync();

        using var reportRun = CreateContext(SystemCurrentUserContext.Instance);
        var admin = new FakeCurrentUserContext(userId: 1, Role.Admin);
        var reportResult = await reportRun.Service(admin).GetAtRiskStudentsAsync();

        var jobFlaggedIds = jobResult.AtRiskStudents.Select(f => f.StudentId).OrderBy(id => id).ToList();
        var reportFlaggedIds = reportResult.Select(f => f.StudentId).OrderBy(id => id).ToList();

        Assert.Equal(new[] { 200, 203 }, jobFlaggedIds);
        Assert.Equal(jobFlaggedIds, reportFlaggedIds);

        foreach (var jobFlag in jobResult.AtRiskStudents)
        {
            var reportFlag = Assert.Single(reportResult, f => f.StudentId == jobFlag.StudentId);
            Assert.Equal(jobFlag.StudentName, reportFlag.StudentName);
            Assert.Equal(jobFlag.LastActivityAt, reportFlag.LastActivityAt);
            Assert.Equal(jobFlag.DaysSinceLastActivity, reportFlag.DaysSinceLastActivity);
        }
    }
}
