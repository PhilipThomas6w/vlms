using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Calendar;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The private calendar subscription (functional.md FR-022, TC-055): the link's secret is kept only
/// as a hash; the feed serves the link's chapter's events, nothing of another chapter and no personal
/// data; a replaced or stopped link, or one whose owner no longer belongs to the chapter, serves
/// nothing; and the iCalendar text is well formed (UK times in UTC, escaping, folding). The chapter's
/// public link: only its Admins make, replace or stop it, everyone in the chapter reads it, and it
/// serves the chapter's parish events only, never a class or excursion.
/// </summary>
public sealed class CalendarFeedServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int Parent = 20;
    private const int Teacher = 10;
    private const int CoventryAdmin = 13;
    private const int Admin = 11;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly SqliteConnection _anchor;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public CalendarFeedServiceTests()
    {
        var connectionString = $"Data Source=file:vlms-feed-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var db = new VlmsDbContext(_options, TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        AddUser(db, Parent, (Role.Parent, Chapter.ShrewsburyId));
        AddUser(db, Teacher, (Role.Teacher, Chapter.ShrewsburyId));
        AddUser(db, CoventryAdmin, (Role.Admin, Coventry));
        AddUser(db, Admin, (Role.Admin, Chapter.ShrewsburyId));
        db.ChapterEvents.Add(new ChapterEvent
        {
            Id = 1, ChapterId = Chapter.ShrewsburyId, Date = Today.AddDays(7), Title = "Chapter meeting, with tea; all welcome",
            StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(16, 0), Location = "Church hall", CreatedByUserId = Teacher, CreatedAt = DateTime.UtcNow
        });
        db.ChapterEvents.Add(new ChapterEvent { Id = 2, ChapterId = Coventry, Date = Today.AddDays(7), Title = "Coventry meeting", CreatedByUserId = CoventryAdmin, CreatedAt = DateTime.UtcNow });
        db.ChapterEvents.Add(new ChapterEvent { Id = 3, ChapterId = Chapter.ShrewsburyId, Date = Today.AddMonths(-13), Title = "Long ago", CreatedByUserId = Teacher, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private static void AddUser(VlmsDbContext db, int id, params (Role Role, int Chapter)[] roles)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = $"user{id}@example.com" });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    private async Task<T> Feeds<T>(ICurrentUserContext user, Func<CalendarFeedService, Task<T>> action)
    {
        await using var db = new VlmsDbContext(_options, user);
        return await action(new CalendarFeedService(db, user, _options));
    }

    // The subscription is fetched by a calendar app: nobody is signed in.
    private Task<string?> Fetch(string secret) =>
        Feeds(new FakeCurrentUserContext(null) { ActiveChapterId = null }, s => s.BuildFeedAsync(secret));

    private static FakeCurrentUserContext As(int userId, Role role, int chapterId = Chapter.ShrewsburyId) =>
        new(userId, role) { ActiveChapterId = chapterId };

    [Fact]
    public async Task ALink_ServesItsChaptersEventsOnly_AndOnlyItsHashIsStored()
    {
        var secret = await Feeds(As(Parent, Role.Parent), s => s.CreateLinkAsync());

        var ics = await Fetch(secret);

        Assert.NotNull(ics);
        Assert.Contains("UID:event-1@varangians.co.uk", ics);
        Assert.DoesNotContain("Coventry meeting", ics);
        Assert.DoesNotContain("Long ago", ics);
        await using var db = new VlmsDbContext(_options, TestSeedUserContext.Instance);
        var stored = Assert.Single(db.CalendarFeedTokens.ToList());
        Assert.Equal((Parent, Chapter.ShrewsburyId), (stored.UserId, stored.ChapterId));
        Assert.NotEqual(secret, stored.TokenHash);
        Assert.DoesNotContain(secret, stored.TokenHash);
        Assert.NotNull(await Feeds(As(Parent, Role.Parent), s => s.GetLinkCreatedAtAsync()));
    }

    [Fact]
    public async Task ANewLink_StopsTheOldOne_AndStoppingALinkStopsIt()
    {
        var first = await Feeds(As(Parent, Role.Parent), s => s.CreateLinkAsync());
        var second = await Feeds(As(Parent, Role.Parent), s => s.CreateLinkAsync());

        Assert.Null(await Fetch(first));
        Assert.NotNull(await Fetch(second));

        await Feeds(As(Parent, Role.Parent), async s => { await s.RemoveLinkAsync(); return 0; });
        Assert.Null(await Fetch(second));
        Assert.Null(await Feeds(As(Parent, Role.Parent), s => s.GetLinkCreatedAtAsync()));
    }

    [Fact]
    public async Task ALink_StopsWorking_WhenItsOwnerNoLongerBelongsToTheChapter()
    {
        var parentLink = await Feeds(As(Parent, Role.Parent), s => s.CreateLinkAsync());
        var teacherLink = await Feeds(As(Teacher, Role.Teacher), s => s.CreateLinkAsync());

        await using (var db = new VlmsDbContext(_options, TestSeedUserContext.Instance))
        {
            db.UserRoles.RemoveRange(db.UserRoles.Where(r => r.UserId == Parent));
            db.Entry(db.AppUsers.Single(u => u.Id == Teacher)).Property(u => u.HasLeft).CurrentValue = true;
            db.SaveChanges();
        }

        Assert.Null(await Fetch(parentLink));
        Assert.Null(await Fetch(teacherLink));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-link")]
    public async Task AnUnknownLink_ServesNothing(string secret) => Assert.Null(await Fetch(secret));

    [Theory]
    [InlineData(Role.Approver)]
    [InlineData(Role.NationalAdministrator)]
    public async Task OnlySomeoneWorkingInTheChapter_CanMakeALink(Role role)
    {
        var national = new FakeCurrentUserContext(Parent, role) { ActiveChapterId = null };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Feeds(national, s => s.CreateLinkAsync()));
    }

    [Fact]
    public void TheFeed_GivesUkTimesInUtc_AllDayEventsAsDates_AndEscapesAndFoldsText()
    {
        var summer = new ChapterEvent
        {
            Id = 7, Date = new DateOnly(2026, 6, 21), Title = "Picnic, games; and a very long description that goes on well past seventy-five octets",
            StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(16, 30), Location = "Quarry Park", CreatedByUserId = 1, CreatedAt = DateTime.UtcNow
        };
        var winter = new ChapterEvent { Id = 8, Date = new DateOnly(2026, 12, 20), Title = "Carols", StartTime = new TimeOnly(14, 0), CreatedByUserId = 1, CreatedAt = DateTime.UtcNow };
        var allDay = new ChapterEvent { Id = 9, Date = new DateOnly(2027, 1, 17), Title = "Retreat", CreatedByUserId = 1, CreatedAt = DateTime.UtcNow };

        var ics = CalendarFeedService.ToICalendar("Varangians", [summer, winter, allDay], new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics);
        Assert.Contains("DTSTART:20260621T130000Z\r\nDTEND:20260621T153000Z", ics);
        Assert.Contains("DTSTART:20261220T140000Z\r\nSUMMARY:Carols", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20270117\r\nDTEND;VALUE=DATE:20270118", ics);
        Assert.Contains("SUMMARY:Picnic\\, games\\; and", ics);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    private Task<string?> FetchPublic(string key) =>
        Feeds(new FakeCurrentUserContext(null) { ActiveChapterId = null }, s => s.BuildPublicFeedAsync(key));

    private void AddEvent(int id, int chapterId, ChapterEventKind kind, string title, int days = 3)
    {
        using var db = new VlmsDbContext(_options, TestSeedUserContext.Instance);
        db.ChapterEvents.Add(new ChapterEvent
        {
            Id = id, ChapterId = chapterId, Date = Today.AddDays(days), Title = title, Kind = kind,
            CreatedByUserId = chapterId == Coventry ? CoventryAdmin : Admin, CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ThePublicLink_ServesTheChaptersParishEventsOnly_NeverAClassOrExcursion()
    {
        AddEvent(4, Chapter.ShrewsburyId, ChapterEventKind.Service, "Divine Liturgy");
        AddEvent(5, Chapter.ShrewsburyId, ChapterEventKind.ParishMeeting, "Parish council", days: 4);
        AddEvent(6, Chapter.ShrewsburyId, ChapterEventKind.Excursion, "Trip to Lichfield", days: 5);
        AddEvent(7, Coventry, ChapterEventKind.Service, "Coventry vespers");
        AddEvent(8, Chapter.ShrewsburyId, ChapterEventKind.Service, "Old service", days: -400);
        var key = await Feeds(As(Admin, Role.Admin), s => s.CreatePublicLinkAsync());

        var ics = await FetchPublic(key);

        Assert.NotNull(ics);
        Assert.Contains("SUMMARY:Divine Liturgy", ics);
        Assert.Contains("SUMMARY:Parish council", ics);
        Assert.DoesNotContain("Chapter meeting", ics);
        Assert.DoesNotContain("Lichfield", ics);
        Assert.DoesNotContain("Coventry", ics);
        Assert.DoesNotContain("Old service", ics);
        Assert.Contains("X-WR-CALNAME:", ics);
    }

    [Fact]
    public async Task OnlyTheChaptersAdmins_MakeOrStopThePublicLink_AndEveryoneInTheChapterSeesIt()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Feeds(As(Teacher, Role.Teacher), s => s.CreatePublicLinkAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Feeds(As(Parent, Role.Parent), s => s.CreatePublicLinkAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await Feeds(As(Teacher, Role.Teacher), async s => { await s.RemovePublicLinkAsync(); return 0; });
        });
        Assert.Null(await Feeds(As(Parent, Role.Parent), s => s.GetPublicLinkKeyAsync()));

        var key = await Feeds(As(Admin, Role.Admin), s => s.CreatePublicLinkAsync());

        Assert.Equal(key, await Feeds(As(Parent, Role.Parent), s => s.GetPublicLinkKeyAsync()));
        Assert.Equal(key, await Feeds(As(Teacher, Role.Teacher), s => s.GetPublicLinkKeyAsync()));
        Assert.Null(await Feeds(As(CoventryAdmin, Role.Admin, Coventry), s => s.GetPublicLinkKeyAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Feeds(new FakeCurrentUserContext(Parent, Role.NationalAdministrator) { ActiveChapterId = null }, s => s.GetPublicLinkKeyAsync()));
    }

    [Fact]
    public async Task ANewPublicLink_StopsTheOldOne_AndStoppingItOrClosingTheChapterStopsIt()
    {
        var first = await Feeds(As(Admin, Role.Admin), s => s.CreatePublicLinkAsync());
        var second = await Feeds(As(Admin, Role.Admin), s => s.CreatePublicLinkAsync());

        Assert.Null(await FetchPublic(first));
        Assert.NotNull(await FetchPublic(second));
        Assert.Null(await FetchPublic(""));
        Assert.Null(await FetchPublic("not-a-real-link"));

        await using (var db = new VlmsDbContext(_options, TestSeedUserContext.Instance))
        {
            db.Entry(db.Chapters.Single(c => c.Id == Chapter.ShrewsburyId)).Property(c => c.IsActive).CurrentValue = false;
            db.SaveChanges();
        }

        Assert.Null(await FetchPublic(second));

        await using (var db = new VlmsDbContext(_options, TestSeedUserContext.Instance))
        {
            db.Entry(db.Chapters.Single(c => c.Id == Chapter.ShrewsburyId)).Property(c => c.IsActive).CurrentValue = true;
            db.SaveChanges();
        }

        await Feeds(As(Admin, Role.Admin), async s => { await s.RemovePublicLinkAsync(); return 0; });
        Assert.Null(await FetchPublic(second));
        Assert.Null(await Feeds(As(Admin, Role.Admin), s => s.GetPublicLinkKeyAsync()));
    }
}
