using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="StudentPageService"/> (functional.md FR-011, quality/test-plan.md TC-021): a
/// signed-in student sees their own rank, progress, completions, badges and certificates, and never
/// another student's; reversed completions and their certificates are left out; other roles are
/// refused.
/// </summary>
public sealed class StudentPageServiceTests : IDisposable
{
    private const int AlexUser = 50;
    private const int SamUser = 60;

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public StudentPageServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-studentpage-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var db = NewContext(NullCurrentUserContext.Instance);
        db.Database.EnsureCreated();

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Scout" });
        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        db.Lessons.Add(new Lesson { Id = 2, RankId = 2, Code = "L2", Title = "Fire Safety", Order = 1, IsActive = true });
        db.Lessons.Add(new Lesson { Id = 3, RankId = 2, Code = "L3", Title = "Map Reading", Order = 2, IsActive = true });
        db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = 2, Tag = LessonTag.Prayers });
        db.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "badges/1.png" });
        db.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher", Email = "t@example.com" });
        db.AppUsers.Add(new AppUser { Id = AlexUser, DisplayName = "Alex", Email = "" });
        db.AppUsers.Add(new AppUser { Id = SamUser, DisplayName = "Sam", Email = "" });

        // Alex: promoted to Scout, one Scout lesson done, one reversed.
        db.Students.Add(new Student
        {
            Id = 100, Name = "Alex", DateOfBirth = new DateOnly(2015, 1, 1), CurrentRankId = 2,
            Status = StudentStatus.Graduated, EnrolmentDate = new DateOnly(2024, 1, 1), AppUserId = AlexUser
        });
        db.StudentLessonCompletions.Add(Completion(1, 100, lessonId: 1, reversed: false));
        db.StudentLessonCompletions.Add(Completion(2, 100, lessonId: 2, reversed: false));
        db.StudentLessonCompletions.Add(Completion(3, 100, lessonId: 3, reversed: true));
        db.Certificates.Add(new Certificate { Id = 1, StudentLessonCompletionId = 1, GeneratedAt = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), BlobKey = "c/1.pdf" });
        db.Certificates.Add(new Certificate { Id = 3, StudentLessonCompletionId = 3, GeneratedAt = new DateTime(2025, 3, 3, 0, 0, 0, DateTimeKind.Utc), BlobKey = "c/3.pdf" });
        db.StudentBadges.Add(new StudentBadge { Id = 1, StudentId = 100, RankBadgeId = 1, AwardedAt = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc) });

        // Sam: a different student with their own completion and certificate.
        db.Students.Add(new Student
        {
            Id = 200, Name = "Sam", DateOfBirth = new DateOnly(2015, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1), AppUserId = SamUser
        });
        db.StudentLessonCompletions.Add(Completion(4, 200, lessonId: 1, reversed: false));
        db.Certificates.Add(new Certificate { Id = 4, StudentLessonCompletionId = 4, GeneratedAt = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc), BlobKey = "c/4.pdf" });
        db.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private static StudentLessonCompletion Completion(int id, int studentId, int lessonId, bool reversed) => new()
    {
        Id = id, StudentId = studentId, LessonId = lessonId, CompletedByUserId = 10,
        CompletedAt = new DateTime(2025, 3, id, 0, 0, 0, DateTimeKind.Utc),
        IsReversed = reversed, ReversedAt = reversed ? new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc) : null
    };

    private VlmsDbContext NewContext(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private async Task<StudentRecord?> PageFor(ICurrentUserContext user)
    {
        using var db = NewContext(user);
        return await new StudentPageService(db, user).GetOwnRecordAsync();
    }

    [Fact]
    public async Task Student_SeesOwnRankProgressCompletionsBadgesAndCertificates()
    {
        var page = await PageFor(new FakeCurrentUserContext(AlexUser, Role.Student));

        Assert.NotNull(page);
        Assert.Equal(100, page!.StudentId);
        Assert.Equal("Scout", page.CurrentRankName);
        Assert.Equal(1, page.CompletedLessons);
        Assert.Equal(2, page.TotalLessons);
        Assert.Equal(["Knots", "Fire Safety"], page.Completions.Select(c => c.LessonTitle));

        var badge = Assert.Single(page.Badges);
        Assert.Equal(new StudentRecordBadge("Recruit", new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), 1, true), badge);

        // The reversed completion's certificate (3) is hidden; Sam's (4) never appears.
        var certificate = Assert.Single(page.Certificates);
        Assert.Equal(1, certificate.CertificateId);
        Assert.Equal("Knots", certificate.LessonTitle);
    }

    [Fact]
    public async Task EachStudent_SeesOnlyTheirOwnRecord()
    {
        var sam = await PageFor(new FakeCurrentUserContext(SamUser, Role.Student));

        Assert.NotNull(sam);
        Assert.Equal(200, sam!.StudentId);
        Assert.Equal([4], sam.Certificates.Select(c => c.CertificateId));
        Assert.Empty(sam.Badges);
        Assert.DoesNotContain(sam.Completions, c => c.LessonTitle == "Fire Safety");
    }

    [Fact]
    public async Task StudentRoleWithNoLinkedRecord_GetsNothing()
    {
        Assert.Null(await PageFor(new FakeCurrentUserContext(999, Role.Student)));
        Assert.Null(await PageFor(new FakeCurrentUserContext(null, Role.Student)));
    }

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.Approver)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task OtherRoles_AreRefused_EvenWithAStudentsUserId(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PageFor(new FakeCurrentUserContext(AlexUser, role)));
    }

    private async Task<StudentJourney?> JourneyFor(ICurrentUserContext user)
    {
        using var db = NewContext(user);
        return await new StudentPageService(db, user).GetOwnJourneyAsync();
    }

    [Fact]
    public async Task Journey_ListsEveryRank_WithItsLessons_AndMarksTheCurrentOne()
    {
        var journey = await JourneyFor(new FakeCurrentUserContext(SamUser, Role.Student));

        Assert.NotNull(journey);
        Assert.Equal(200, journey!.StudentId);
        Assert.False(journey.Graduated);
        Assert.Equal(["Recruit", "Scout"], journey.Ranks.Select(r => r.Name));
        Assert.Equal([JourneyRankState.Current, JourneyRankState.Ahead], journey.Ranks.Select(r => r.State));

        var recruit = journey.Ranks[0];
        var knots = Assert.Single(recruit.Lessons);
        Assert.Equal(new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc), knots.CompletedAt);
        Assert.Equal(4, knots.CertificateId);
        Assert.Equal(1, recruit.CompletedCount);
        Assert.Null(recruit.BadgeAwardedAt);
        Assert.True(recruit.HasBadgeImage);

        var scout = journey.Ranks[1];
        Assert.Equal(["Fire Safety", "Map Reading"], scout.Lessons.Select(l => l.Title));
        Assert.Equal([LessonTag.Prayers], scout.Lessons[0].Tags);
        Assert.All(scout.Lessons, l => Assert.Null(l.CompletedAt));
        Assert.False(scout.HasBadgeImage);

        Assert.Same(recruit, journey.Current);
        Assert.Null(journey.NextLesson);
    }

    [Fact]
    public async Task Journey_LeavesOutReversedCompletions_AndTheirCertificates_AndShowsBadgesEarned()
    {
        var journey = await JourneyFor(new FakeCurrentUserContext(AlexUser, Role.Student));

        Assert.NotNull(journey);
        Assert.True(journey!.Graduated);
        Assert.All(journey.Ranks, r => Assert.Equal(JourneyRankState.Past, r.State));
        Assert.Equal(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), journey.Ranks[0].BadgeAwardedAt);

        var scout = journey.Ranks[1];
        Assert.NotNull(scout.Lessons.Single(l => l.Title == "Fire Safety").CompletedAt);
        var mapReading = scout.Lessons.Single(l => l.Title == "Map Reading");
        Assert.Null(mapReading.CompletedAt);
        Assert.Null(mapReading.CertificateId);

        // Sam's completion of Knots (certificate 4) is not Alex's.
        Assert.Equal(1, journey.Ranks[0].Lessons.Single().CertificateId);
    }

    [Fact]
    public async Task Journey_NextLesson_IsTheFirstIncompleteLessonOfTheCurrentRank()
    {
        using (var db = NewContext(NullCurrentUserContext.Instance))
        {
            var sam = db.Students.Single(s => s.Id == 200);
            db.Entry(sam).Property(s => s.CurrentRankId).CurrentValue = 2;
            db.StudentLessonCompletions.Add(Completion(5, 200, lessonId: 2, reversed: false));
            db.SaveChanges();
        }

        var journey = await JourneyFor(new FakeCurrentUserContext(SamUser, Role.Student));

        Assert.Equal([JourneyRankState.Past, JourneyRankState.Current], journey!.Ranks.Select(r => r.State));
        Assert.Equal("Map Reading", journey.NextLesson?.Title);
    }

    [Fact]
    public async Task Journey_StudentRoleWithNoLinkedRecord_GetsNothing()
    {
        Assert.Null(await JourneyFor(new FakeCurrentUserContext(999, Role.Student)));
    }

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    public async Task Journey_OtherRoles_AreRefused_EvenWithAStudentsUserId(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => JourneyFor(new FakeCurrentUserContext(AlexUser, role)));
    }
}
