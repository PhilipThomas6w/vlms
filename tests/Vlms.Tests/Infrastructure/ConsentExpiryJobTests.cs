using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the ConsentExpiryJob WebJob's domain logic (docs/design/low-level-design.md
/// "ConsentExpiryJob", adr/0003-scheduled-jobs-webjobs.md, STATE.md): consent/DBS expiry
/// flagging+escalation and the 8-week at-risk/disengaged-student flag (functional.md,
/// quality/test-plan.md TC-009/TC-010). See ConsentExpiryJob's own doc comment for the documented
/// judgement calls this test suite exercises (the 28-day default warning window, "escalation" as an
/// elevated-severity log entry, Active-only scoping, the "no record at all" = expired treatment).
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="SensitiveDataAccessControlTests"/> — this
/// job legitimately reads <see cref="DbsCheck"/> (a whole-entity-restricted, read-audited entity),
/// so the same real-SQLite/DI-resolved-context requirements apply.
/// </summary>
public sealed class ConsentExpiryJobTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ConsentExpiryJobTests()
    {
        _connectionString = $"Data Source=file:vlms-expiryjob-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

    private static ConsentRecord NewConsent(int id, int studentId, DateOnly expiryDate) => new()
    {
        Id = id,
        StudentId = studentId,
        PeriodStart = expiryDate.AddYears(-1),
        PeriodEnd = expiryDate,
        PhotoMediaConsent = true,
        TransportOffsiteConsent = true,
        DataSharingConsent = true,
        Status = ConsentStatus.Approved,
        SubmittedByParentId = 1,
        ExpiryDate = expiryDate
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

    [Fact]
    public async Task RunAsync_FlagsConsentExpiringSoon_Expired_AndMissing_ButNotBeyondWindowOrInactive()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            seed.Context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "j@example.com", IsPrimary = true });

            seed.Context.Students.Add(NewStudent(100, "Expiring Soon", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(1, 100, today.AddDays(10))); // within 28-day default window

            seed.Context.Students.Add(NewStudent(101, "Already Expired", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(2, 101, today.AddDays(-5)));

            seed.Context.Students.Add(NewStudent(102, "Never Had Consent", StudentStatus.Active, today.AddYears(-1)));

            seed.Context.Students.Add(NewStudent(103, "Comfortably Valid", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(3, 103, today.AddDays(60))); // beyond the 28-day window

            seed.Context.Students.Add(NewStudent(104, "Graduated With Expired Consent", StudentStatus.Graduated, today.AddYears(-2)));
            seed.Context.ConsentRecords.Add(NewConsent(4, 104, today.AddDays(-100)));

            seed.Context.SaveChanges();
        }

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var sut = new ConsentExpiryJob(run.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>());

        var result = await sut.RunAsync();

        var expiringSoon = Assert.Single(result.ConsentFlags, f => f.StudentId == 100);
        Assert.False(expiringSoon.IsExpired);
        Assert.Equal(1, expiringSoon.ConsentRecordId);

        var expired = Assert.Single(result.ConsentFlags, f => f.StudentId == 101);
        Assert.True(expired.IsExpired);
        Assert.Equal(2, expired.ConsentRecordId);

        var missing = Assert.Single(result.ConsentFlags, f => f.StudentId == 102);
        Assert.True(missing.IsExpired);
        Assert.Null(missing.ConsentRecordId);
        Assert.Null(missing.ExpiryDate);

        Assert.DoesNotContain(result.ConsentFlags, f => f.StudentId == 103);
        Assert.DoesNotContain(result.ConsentFlags, f => f.StudentId == 104);
    }

    [Fact]
    public async Task RunAsync_FlagsDbsExpiringSoon_Expired_Missing_AndFlaggedStatus_ButNotValid()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            seed.Context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "t10", DisplayName = "Teacher Soon", Email = "t10@example.com" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
            seed.Context.DbsChecks.Add(NewDbsCheck(1, 10, today.AddDays(10)));

            seed.Context.AppUsers.Add(new AppUser { Id = 11, EntraObjectId = "t11", DisplayName = "Teacher Expired", Email = "t11@example.com" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 11, Role = Role.Teacher });
            seed.Context.DbsChecks.Add(NewDbsCheck(2, 11, today.AddDays(-5)));

            seed.Context.AppUsers.Add(new AppUser { Id = 12, EntraObjectId = "t12", DisplayName = "Teacher Never Checked", Email = "t12@example.com" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 12, Role = Role.Teacher });

            seed.Context.AppUsers.Add(new AppUser { Id = 13, EntraObjectId = "t13", DisplayName = "Teacher Valid", Email = "t13@example.com" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 13, Role = Role.Teacher });
            seed.Context.DbsChecks.Add(NewDbsCheck(3, 13, today.AddDays(365)));

            seed.Context.AppUsers.Add(new AppUser { Id = 14, EntraObjectId = "t14", DisplayName = "Teacher Only Flagged", Email = "t14@example.com" });
            seed.Context.UserRoles.Add(new UserRole { UserId = 14, Role = Role.Teacher });
            seed.Context.DbsChecks.Add(NewDbsCheck(4, 14, today.AddDays(365), DbsCheckStatus.Flagged));

            seed.Context.SaveChanges();
        }

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var sut = new ConsentExpiryJob(run.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>());

        var result = await sut.RunAsync();

        var expiringSoon = Assert.Single(result.DbsFlags, f => f.TeacherUserId == 10);
        Assert.False(expiringSoon.IsExpired);

        var expired = Assert.Single(result.DbsFlags, f => f.TeacherUserId == 11);
        Assert.True(expired.IsExpired);

        var missing = Assert.Single(result.DbsFlags, f => f.TeacherUserId == 12);
        Assert.True(missing.IsExpired);
        Assert.Null(missing.DbsCheckId);

        Assert.DoesNotContain(result.DbsFlags, f => f.TeacherUserId == 13);

        var onlyFlagged = Assert.Single(result.DbsFlags, f => f.TeacherUserId == 14);
        Assert.True(onlyFlagged.IsExpired);
    }

    [Fact]
    public async Task RunAsync_FlagsAtRiskStudents_ByCompletionGap_OrEnrolmentIfNever_ButGivesNewEnrolmentsAFairChance()
    {
        var now = DateTime.UtcNow;

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            seed.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "b1", IsActive = true });
            seed.Context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "t10", DisplayName = "Teacher One", Email = "t10@example.com" });

            // Disengaged: last completion 70 days ago (> 56-day threshold).
            seed.Context.Students.Add(NewStudent(200, "Disengaged", StudentStatus.Active, DateOnly.FromDateTime(now.AddYears(-1))));
            seed.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = 1, StudentId = 200, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-70), IsReversed = false
            });

            // Recently active: last completion 30 days ago (< threshold) — not at-risk.
            seed.Context.Students.Add(NewStudent(201, "Recently Active", StudentStatus.Active, DateOnly.FromDateTime(now.AddYears(-1))));
            seed.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = 2, StudentId = 201, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-30), IsReversed = false
            });

            // Newly enrolled, no completions yet — fair chance, not at-risk.
            seed.Context.Students.Add(NewStudent(202, "New Enrolment", StudentStatus.Active, DateOnly.FromDateTime(now.AddDays(-10))));

            // Enrolled long ago, never completed anything — at-risk from enrolment date.
            seed.Context.Students.Add(NewStudent(203, "Long-Enrolled Never Completed", StudentStatus.Active, DateOnly.FromDateTime(now.AddDays(-100))));

            // Only reversed completions count as "never completed" — still at-risk.
            seed.Context.Students.Add(NewStudent(204, "Only Reversed Completion", StudentStatus.Active, DateOnly.FromDateTime(now.AddDays(-100))));
            seed.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = 3, StudentId = 204, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-10), IsReversed = true
            });

            // Inactive student, disengaged by date, but out of scope (Active-only).
            seed.Context.Students.Add(NewStudent(205, "Inactive Disengaged", StudentStatus.Inactive, DateOnly.FromDateTime(now.AddYears(-1))));
            seed.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = 4, StudentId = 205, LessonId = 1, CompletedByUserId = 10, CompletedAt = now.AddDays(-90), IsReversed = false
            });

            seed.Context.SaveChanges();
        }

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var sut = new ConsentExpiryJob(run.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>());

        var result = await sut.RunAsync();

        Assert.Single(result.AtRiskStudents, f => f.StudentId == 200);
        Assert.DoesNotContain(result.AtRiskStudents, f => f.StudentId == 201);
        Assert.DoesNotContain(result.AtRiskStudents, f => f.StudentId == 202);
        Assert.Single(result.AtRiskStudents, f => f.StudentId == 203);
        Assert.Single(result.AtRiskStudents, f => f.StudentId == 204);
        Assert.DoesNotContain(result.AtRiskStudents, f => f.StudentId == 205);
    }

    [Fact]
    public async Task RunAsync_LogsExpiredConsentAtError_AndExpiringSoonAtWarning()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            seed.Context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "j@example.com", IsPrimary = true });
            seed.Context.Students.Add(NewStudent(300, "Expiring Soon", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(1, 300, today.AddDays(10)));
            seed.Context.Students.Add(NewStudent(301, "Expired", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(2, 301, today.AddDays(-5)));
            seed.Context.SaveChanges();
        }

        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var logger = new ListLogger<ConsentExpiryJob>();
        var sut = new ConsentExpiryJob(run.Context, SystemCurrentUserContext.Instance, logger);

        await sut.RunAsync();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ESCALATION") && e.Message.Contains("301"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("300"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("300"));
    }

    [Fact]
    public async Task RunAsync_ExpiryWindow_IsConfigurable()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var seed = CreateContext(SystemCurrentUserContext.Instance))
        {
            seed.Context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "j@example.com", IsPrimary = true });
            seed.Context.Students.Add(NewStudent(400, "Expires In 40 Days", StudentStatus.Active, today.AddYears(-1)));
            seed.Context.ConsentRecords.Add(NewConsent(1, 400, today.AddDays(40)));
            seed.Context.SaveChanges();
        }

        using var defaultWindow = CreateContext(SystemCurrentUserContext.Instance);
        var defaultResult = await new ConsentExpiryJob(defaultWindow.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>())
            .RunAsync();
        Assert.DoesNotContain(defaultResult.ConsentFlags, f => f.StudentId == 400); // 40 days > default 28-day window

        using var widerWindow = CreateContext(SystemCurrentUserContext.Instance);
        var widerResult = await new ConsentExpiryJob(
                widerWindow.Context, SystemCurrentUserContext.Instance, new ListLogger<ConsentExpiryJob>(), expiryWarningWindowDays: 60)
            .RunAsync();
        Assert.Single(widerResult.ConsentFlags, f => f.StudentId == 400);
    }

    [Fact]
    public async Task RunAsync_ByCallerWithoutAdminOrSafeguardingOfficerRole_Throws()
    {
        var noRoleCaller = new FakeCurrentUserContext(userId: null);
        using var run = CreateContext(noRoleCaller);
        var sut = new ConsentExpiryJob(run.Context, noRoleCaller, new ListLogger<ConsentExpiryJob>());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RunAsync());
    }
}
