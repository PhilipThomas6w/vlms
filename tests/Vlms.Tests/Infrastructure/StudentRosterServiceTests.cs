using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Engagement;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="StudentRosterService"/> (FR-009, TC-019): active-only, "assigned to me",
/// progress (reversed completions and inactive lessons do not count) and the role check.
/// </summary>
public sealed class StudentRosterServiceTests : IDisposable
{
    private static readonly DateTime T = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public StudentRosterServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-roster-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = CreateDb(new FakeCurrentUserContext(1, Role.Admin));
        db.Database.EnsureCreated();

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Squire" });
        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        db.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "Fire", Order = 2, IsActive = true });
        db.Lessons.Add(new Lesson { Id = 3, RankId = 1, Code = "L3", Title = "Maps", Order = 3, IsActive = true });
        db.Lessons.Add(new Lesson { Id = 4, RankId = 1, Code = "L4", Title = "Retired", Order = 4, IsActive = false });
        db.Lessons.Add(new Lesson { Id = 5, RankId = 2, Code = "L5", Title = "Archery", Order = 1, IsActive = true });
        db.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "b.png" });
        db.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher", Email = "t@example.com" });
        db.AppUsers.Add(new AppUser { Id = 11, DisplayName = "Other", Email = "o@example.com" });

        // Alex: mine, Recruit; Knots done, Fire reversed, retired lesson done (not counted).
        db.Students.Add(NewStudent(100, "Alex", StudentStatus.Active, 10));
        db.Students.Add(NewStudent(200, "Bea", StudentStatus.Active, 11));
        db.Students.Add(NewStudent(300, "Cal", StudentStatus.Inactive, 10));
        db.Students.Add(NewStudent(400, "Dee", StudentStatus.Graduated, null));
        db.StudentLessonCompletions.Add(NewCompletion(1, 100, 1, reversed: false));
        db.StudentLessonCompletions.Add(NewCompletion(2, 100, 2, reversed: true));
        db.StudentLessonCompletions.Add(NewCompletion(3, 100, 4, reversed: false));
        db.StudentBadges.Add(new StudentBadge { Id = 1, StudentId = 100, RankBadgeId = 1, AwardedAt = T });
        db.Certificates.Add(new Certificate { Id = 1, StudentLessonCompletionId = 1, GeneratedAt = T, BlobKey = "c1.pdf" });
        db.Certificates.Add(new Certificate { Id = 2, StudentLessonCompletionId = 2, GeneratedAt = T, BlobKey = "c2.pdf" });
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Parent", ContactInfo = "p@example.com", IsPrimary = true });
        db.ConsentRecords.Add(new ConsentRecord
        {
            Id = 1, StudentId = 100, PeriodStart = new DateOnly(2025, 1, 1), PeriodEnd = new DateOnly(2026, 1, 1),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = 1, ExpiryDate = new DateOnly(2999, 12, 31)
        });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext CreateDb(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private static Student NewStudent(int id, string name, StudentStatus status, int? teacher) => new()
    {
        Id = id, Name = name, DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
        Status = status, EnrolmentDate = new DateOnly(2024, 1, 1), AssignedTeacherUserId = teacher
    };

    private static StudentLessonCompletion NewCompletion(int id, int studentId, int lessonId, bool reversed) => new()
    {
        Id = id, StudentId = studentId, LessonId = lessonId, CompletedByUserId = 10, CompletedAt = T,
        IsReversed = reversed, ReversedAt = reversed ? T : null
    };

    private async Task<T> Run<T>(ICurrentUserContext user, Func<StudentRosterService, Task<T>> action)
    {
        using var db = CreateDb(user);
        return await action(new StudentRosterService(db, user, new InMemoryBlobStorage()));
    }

    [Fact]
    public async Task List_ShowsOnlyActiveStudents_WithProgress()
    {
        var teacher = new FakeCurrentUserContext(10, Role.Teacher);
        var list = await Run(teacher, s => s.ListActiveStudentsAsync());

        Assert.Equal(["Alex", "Bea"], list.Select(x => x.StudentName));
        var alex = list.Single(x => x.StudentId == 100);
        Assert.Equal("Recruit", alex.CurrentRankName);
        Assert.Equal(1, alex.CompletedLessons);
        Assert.Equal(3, alex.TotalLessons);
        Assert.Equal(0, list.Single(x => x.StudentId == 200).CompletedLessons);
    }

    [Fact]
    public async Task List_AssignedToMe_FiltersToCallersStudents()
    {
        var teacher = new FakeCurrentUserContext(10, Role.Teacher);
        var list = await Run(teacher, s => s.ListActiveStudentsAsync(assignedToMeOnly: true));

        Assert.Equal([100], list.Select(x => x.StudentId));
    }

    [Fact]
    public async Task List_Admin_IsAllowed()
    {
        var list = await Run(new FakeCurrentUserContext(1, Role.Admin), s => s.ListActiveStudentsAsync());

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task List_SafeguardingOfficer_IsAllowed_ToReadAttendance()
    {
        var list = await Run(new FakeCurrentUserContext(10, Role.SafeguardingOfficer), s => s.ListActiveStudentsAsync());

        Assert.Equal(2, list.Count);
    }

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.Approver)]
    public async Task Roles_OtherThanTeacherAdminAndSafeguardingOfficer_AreRefused(Role role)
    {
        var user = new FakeCurrentUserContext(10, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(user, s => s.ListActiveStudentsAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(user, s => s.GetRecordAsync(100)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(user, s => s.GetJourneyAsync(100)));
    }

    [Fact]
    public async Task Record_ShowsCompletionsBadgesCertificatesAndConsent_ExcludingReversed()
    {
        var teacher = new FakeCurrentUserContext(10, Role.Teacher);
        var record = await Run(teacher, s => s.GetRecordAsync(100));

        Assert.NotNull(record);
        Assert.Equal(1, record.CompletedLessons);
        Assert.Equal(3, record.TotalLessons);
        Assert.Equal(["Knots", "Retired"], record.Completions.Select(c => c.LessonTitle));
        Assert.Single(record.Badges, b => b.RankName == "Recruit");
        var certificate = Assert.Single(record.Certificates);
        Assert.Equal(1, certificate.CertificateId);
        Assert.Equal("Recruit", certificate.RankName);
        Assert.True(record.HasCurrentConsent);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(999)]
    public async Task Record_NotActiveOrUnknown_ReturnsNull(int studentId)
    {
        var teacher = new FakeCurrentUserContext(10, Role.Teacher);

        Assert.Null(await Run(teacher, s => s.GetRecordAsync(studentId)));
    }

    [Fact]
    public async Task Journey_ShowsEveryRank_WithTheCurrentOne_AndLeavesOutReversedCompletions()
    {
        var teacher = new FakeCurrentUserContext(10, Role.Teacher);
        var journey = await Run(teacher, s => s.GetJourneyAsync(100));

        Assert.NotNull(journey);
        Assert.Equal(["Recruit", "Squire"], journey.Ranks.Select(r => r.Name));
        Assert.Equal([JourneyRankState.Current, JourneyRankState.Ahead], journey.Ranks.Select(r => r.State));
        var current = journey.Current!;
        Assert.Equal(1, current.CompletedCount);
        Assert.Equal(3, current.Lessons.Count);
        Assert.Equal(T, current.BadgeAwardedAt);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(999)]
    public async Task Journey_NotActiveOrUnknown_ReturnsNull(int studentId)
    {
        var admin = new FakeCurrentUserContext(1, Role.Admin);

        Assert.Null(await Run(admin, s => s.GetJourneyAsync(studentId)));
    }

    [Fact]
    public async Task List_ShowsRecentAttendance_FromTheLatestThreeRegisters()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            // Alex: absent four meetings ago (not counted), then present, absent, present.
            for (var i = 0; i < 4; i++)
            {
                var date = today.AddDays(-7 * (4 - i));
                db.ChapterEvents.Add(new ChapterEvent { Id = i + 1, Date = date, Title = "Chapter meeting", CreatedByUserId = 10, CreatedAt = DateTime.UtcNow });
                db.AttendanceRegisters.Add(new AttendanceRegister { Id = i + 1, ChapterEventId = i + 1, RankId = 1, MeetingDate = date, TakenByUserId = 10, TakenAt = DateTime.UtcNow });
                db.AttendanceMarks.Add(new AttendanceMark { Id = i + 1, AttendanceRegisterId = i + 1, StudentId = 100, MeetingDate = date, Present = i % 2 == 1 });
            }

            db.SaveChanges();
        }

        var list = await Run(new FakeCurrentUserContext(10, Role.Teacher), s => s.ListActiveStudentsAsync());

        var alex = list.Single(x => x.StudentId == 100);
        Assert.Equal((2, 3), (alex.RecentAttended, alex.RecentMeetings));
        Assert.Equal((0, 0), list.Single(x => x.StudentId == 200) is var bea ? (bea.RecentAttended, bea.RecentMeetings) : default);
    }
}
