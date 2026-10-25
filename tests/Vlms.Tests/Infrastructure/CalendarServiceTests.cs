using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Attendance;
using Vlms.Infrastructure.Calendar;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The calendar and class teachers (functional.md FR-022, TC-054): who reads the calendar (everyone
/// working in the chapter, nobody else), the month view's feasts, fasts and events, only the chapter's
/// Admins scheduling, changing and removing events (one a day, never moving or removing one with a
/// register), events kept to their chapter, and Admins assigning only the chapter's Teachers to classes.
/// </summary>
public sealed class CalendarServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int Teacher = 10;
    private const int Admin = 11;
    private const int Officer = 12;
    private const int CoventryAdmin = 13;
    private const int CoventryTeacher = 14;
    private const int LeftTeacher = 15;
    private const int Parent = 20;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public CalendarServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-calendar-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = CreateDb(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Guardsman" });
        AddUser(db, Teacher, "Teacher", false, (Role.Teacher, Chapter.ShrewsburyId));
        AddUser(db, Admin, "Admin", false, (Role.Admin, Chapter.ShrewsburyId));
        AddUser(db, Officer, "Officer", false, (Role.SafeguardingOfficer, Chapter.ShrewsburyId));
        AddUser(db, CoventryAdmin, "Coventry admin", false, (Role.Admin, Coventry));
        AddUser(db, CoventryTeacher, "Coventry teacher", false, (Role.Teacher, Coventry));
        AddUser(db, LeftTeacher, "Left teacher", true, (Role.Teacher, Chapter.ShrewsburyId));
        AddUser(db, Parent, "Parent", false, (Role.Parent, Chapter.ShrewsburyId));
        db.Students.Add(new Student
        {
            Id = 100, ChapterId = Chapter.ShrewsburyId, Name = "Alex", DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = Today.AddYears(-1)
        });
        db.LiturgicalDays.Add(new LiturgicalDay { Date = Today, Title = "Feast of the day", Fast = LiturgicalFast.Fish, Saints = "Saint A; Saint B" });
        db.LiturgicalReadings.Add(new LiturgicalReading { Id = 1, Date = Today, Order = 1, Heading = "Gospel Reading", Reference = "Luke 8:26-39", Text = "At that time." });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext CreateDb(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private static void AddUser(VlmsDbContext db, int id, string name, bool hasLeft, params (Role Role, int Chapter)[] roles)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com", HasLeft = hasLeft });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    private static FakeCurrentUserContext As(int userId, Role role, int? chapterId = Chapter.ShrewsburyId) =>
        new(userId, role) { ActiveChapterId = chapterId };

    private async Task<T> Calendar<T>(ICurrentUserContext user, Func<CalendarService, Task<T>> action)
    {
        using var db = CreateDb(user);
        return await action(new CalendarService(db, user));
    }

    private async Task Calendar(ICurrentUserContext user, Func<CalendarService, Task> action)
    {
        using var db = CreateDb(user);
        await action(new CalendarService(db, user));
    }

    private async Task<T> Classes<T>(ICurrentUserContext user, Func<ClassTeacherService, Task<T>> action)
    {
        using var db = CreateDb(user);
        return await action(new ClassTeacherService(db, user));
    }

    private Task<int> Create(DateOnly date, string title = "Chapter meeting", int admin = Admin, int chapterId = Chapter.ShrewsburyId) =>
        Calendar(As(admin, Role.Admin, chapterId), s => s.CreateEventAsync(new EventInput(date, title, new TimeOnly(14, 0), new TimeOnly(16, 0), "Church hall")));

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    public async Task EveryoneWorkingInTheChapter_ReadsTheMonth_WithFeastsFastsAndEvents(Role role)
    {
        var id = await Create(Today);

        var month = await Calendar(As(Parent, role), s => s.GetMonthAsync(Today.Year, Today.Month));

        Assert.Equal(DateTime.DaysInMonth(Today.Year, Today.Month), month.Days.Count);
        var today = month.Days.Single(d => d.Date == Today);
        Assert.Equal(("Feast of the day", LiturgicalFast.Fish), (today.Feast, today.Fast));
        Assert.Equal([id], today.Events.Select(e => e.Id));
        var detail = await Calendar(As(Parent, role), s => s.GetLiturgicalDayAsync(Today));
        Assert.Equal(["Saint A", "Saint B"], detail!.Saints);
        Assert.Equal("Luke 8:26-39", Assert.Single(detail.Readings).Reference);
    }

    [Theory]
    [InlineData(Role.Approver)]
    [InlineData(Role.NationalAdministrator)]
    public async Task ARoleWithNoChapterStanding_CannotReadTheCalendar(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Officer, role, chapterId: null), s => s.GetMonthAsync(Today.Year, Today.Month)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Officer, role), s => s.GetLiturgicalDayAsync(Today)));
    }

    [Fact]
    public async Task TheCalendarView_IsRememberedPerPerson_AndUnsetUntilChosen()
    {
        Assert.Null(await Calendar(As(Parent, Role.Parent), s => s.GetViewAsync()));

        await Calendar(As(Parent, Role.Parent), s => s.SetViewAsync(CalendarView.Grid));
        Assert.Equal(CalendarView.Grid, await Calendar(As(Parent, Role.Parent), s => s.GetViewAsync()));
        await Calendar(As(Parent, Role.Parent), s => s.SetViewAsync(CalendarView.List));
        Assert.Equal(CalendarView.List, await Calendar(As(Parent, Role.Parent), s => s.GetViewAsync()));

        // Another person's choice is their own; the same person's choice follows them to another chapter.
        Assert.Null(await Calendar(As(Teacher, Role.Teacher), s => s.GetViewAsync()));
        Assert.Equal(CalendarView.List, await Calendar(As(Parent, Role.Parent, Coventry), s => s.GetViewAsync()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Calendar(As(Parent, Role.Parent), s => s.SetViewAsync((CalendarView)99)));
    }

    [Theory]
    [InlineData(Role.Approver)]
    [InlineData(Role.NationalAdministrator)]
    public async Task ARoleWithNoChapterStanding_CannotChooseACalendarView(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Officer, role, chapterId: null), s => s.GetViewAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Officer, role, chapterId: null), s => s.SetViewAsync(CalendarView.Grid)));
    }

    [Fact]
    public async Task AnotherChapter_NeitherSeesNorChangesThisChaptersEvents()
    {
        var id = await Create(Today);
        await Create(Today, "Coventry meeting", CoventryAdmin, Coventry);

        var coventry = await Calendar(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetMonthAsync(Today.Year, Today.Month));
        Assert.Equal(["Coventry meeting"], coventry.Days.SelectMany(d => d.Events).Select(e => e.Title));
        Assert.Null(await Calendar(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetEventAsync(id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(CoventryAdmin, Role.Admin, Coventry),
            s => s.UpdateEventAsync(id, new EventInput(Today, "Taken over", null, null, null))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(CoventryAdmin, Role.Admin, Coventry),
            s => s.DeleteEventAsync(id)));

        using var db = CreateDb(TestSeedUserContext.Instance);
        Assert.Equal("Chapter meeting", db.ChapterEvents.Single(e => e.Id == id).Title);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.NationalAdministrator)]
    public async Task OnlyAdmins_ScheduleChangeOrRemoveEvents(Role role)
    {
        var id = await Create(Today);
        var input = new EventInput(Today.AddDays(1), "Changed", null, null, null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Teacher, role), s => s.CreateEventAsync(input)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Teacher, role), s => s.UpdateEventAsync(id, input)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Calendar(As(Teacher, role), s => s.DeleteEventAsync(id)));
    }

    [Fact]
    public async Task AnAdmin_SchedulesChangesAndRemovesAnEvent()
    {
        var id = await Create(Today.AddDays(7));
        var created = await Calendar(As(Admin, Role.Admin), s => s.GetEventAsync(id));
        Assert.Equal(("Chapter meeting", new TimeOnly(14, 0), "Church hall", false), (created!.Title, created.StartTime, created.Location, created.HasRegisters));

        await Calendar(As(Admin, Role.Admin), async s => { await s.UpdateEventAsync(id, new EventInput(Today.AddDays(8), "  Retreat  ", null, null, " ")); return 0; });
        var changed = await Calendar(As(Admin, Role.Admin), s => s.GetEventAsync(id));
        Assert.Equal((Today.AddDays(8), "Retreat", (TimeOnly?)null, (string?)null), (changed!.Date, changed.Title, changed.StartTime, changed.Location));

        await Calendar(As(Admin, Role.Admin), async s => { await s.DeleteEventAsync(id); return 0; });
        Assert.Null(await Calendar(As(Admin, Role.Admin), s => s.GetEventAsync(id)));
    }

    [Fact]
    public async Task AChapterMeetsAtMostOnceADay()
    {
        var first = await Create(Today);
        var second = await Create(Today.AddDays(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(Today));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin),
            s => s.UpdateEventAsync(second, new EventInput(Today, "Moved", null, null, null))));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task AnEventWithARegister_KeepsItsDate_AndCannotBeRemoved()
    {
        var id = await Create(Today);
        using (var db = CreateDb(As(Teacher, Role.Teacher)))
        {
            await new AttendanceService(db, As(Teacher, Role.Teacher)).SaveRegisterAsync(id, 1, new Dictionary<int, bool> { [100] = true });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin),
            s => s.UpdateEventAsync(id, new EventInput(Today.AddDays(-1), "Moved", null, null, null))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin),
            s => s.DeleteEventAsync(id)));

        await Calendar(As(Admin, Role.Admin), async s => { await s.UpdateEventAsync(id, new EventInput(Today, "Renamed", null, null, null)); return 0; });
        Assert.True((await Calendar(As(Admin, Role.Admin), s => s.GetEventAsync(id)))!.HasRegisters);
    }

    [Theory]
    [InlineData("", "14:00", "16:00")]
    [InlineData("Meeting", "16:00", "14:00")]
    [InlineData("Meeting", null, "16:00")]
    public async Task AnInvalidEvent_IsRefused(string title, string? start, string? end)
    {
        var input = new EventInput(Today, title, start is null ? null : TimeOnly.Parse(start), end is null ? null : TimeOnly.Parse(end), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(input)));
    }

    [Fact]
    public async Task AnAdmin_AssignsOnlyTheChaptersCurrentTeachersToClasses()
    {
        await Classes(As(Admin, Role.Admin), async s => { await s.AssignAsync(2, Teacher); await s.AssignAsync(2, Teacher); return 0; });

        var classes = await Classes(As(Admin, Role.Admin), s => s.ListAsync());
        Assert.Equal(["Recruit", "Guardsman"], classes.Select(c => c.RankName));
        Assert.Equal(["Teacher"], classes[1].Teachers.Select(t => t.DisplayName));
        Assert.Equal([Teacher], (await Classes(As(Admin, Role.Admin), s => s.ListTeachersAsync())).Select(t => t.UserId));

        foreach (var notATeacherHere in new[] { Parent, LeftTeacher, CoventryTeacher, Admin })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Classes(As(Admin, Role.Admin), async s => { await s.AssignAsync(1, notATeacherHere); return 0; }));
        }

        await Classes(As(Admin, Role.Admin), async s => { await s.RemoveAsync(2, Teacher); return 0; });
        Assert.Empty((await Classes(As(Admin, Role.Admin), s => s.ListAsync()))[1].Teachers);
    }

    [Fact]
    public async Task ClassTeachers_AreKeptToTheirChapter_AndManagedByItsAdminsOnly()
    {
        await Classes(As(Admin, Role.Admin), async s => { await s.AssignAsync(1, Teacher); return 0; });

        Assert.All(await Classes(As(CoventryAdmin, Role.Admin, Coventry), s => s.ListAsync()), c => Assert.Empty(c.Teachers));
        foreach (var role in new[] { Role.Teacher, Role.SafeguardingOfficer, Role.Parent, Role.NationalAdministrator })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Classes(As(Teacher, role), s => s.ListAsync()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Classes(As(Teacher, role), async s => { await s.AssignAsync(1, Teacher); return 0; }));
        }
    }

    [Fact]
    public void RepeatDates_FollowEachPattern_UpToTheLastDate()
    {
        var thirdSunday = new DateOnly(2026, 9, 20);
        Assert.Equal(
            [new(2026, 9, 20), new(2026, 10, 18), new(2026, 11, 15), new(2026, 12, 20), new(2027, 1, 17), new(2027, 2, 21),
             new(2027, 3, 21), new(2027, 4, 18), new(2027, 5, 16), new DateOnly(2027, 6, 20)],
            CalendarService.RepeatDates(thirdSunday, new EventRepeat(RepeatPattern.MonthlyByWeekday, new DateOnly(2027, 6, 30))));
        Assert.Equal(
            [new(2026, 10, 1), new(2026, 10, 8), new(2026, 10, 15), new DateOnly(2026, 10, 22)],
            CalendarService.RepeatDates(new DateOnly(2026, 10, 1), new EventRepeat(RepeatPattern.Weekly, new DateOnly(2026, 10, 28))));
        Assert.Equal(
            [new(2028, 2, 29), new DateOnly(2032, 2, 29)],
            CalendarService.RepeatDates(new DateOnly(2028, 2, 29), new EventRepeat(RepeatPattern.Yearly, new DateOnly(2033, 1, 1))));
        // A fifth Sunday falls only in months that have one.
        Assert.Equal(
            [new(2026, 8, 30), new DateOnly(2026, 11, 29)],
            CalendarService.RepeatDates(new DateOnly(2026, 8, 30), new EventRepeat(RepeatPattern.MonthlyByWeekday, new DateOnly(2026, 12, 31))));

        Assert.Throws<InvalidOperationException>(() => CalendarService.RepeatDates(thirdSunday, new EventRepeat(RepeatPattern.Weekly, thirdSunday.AddDays(-1))));
        Assert.Throws<InvalidOperationException>(() => CalendarService.RepeatDates(thirdSunday, new EventRepeat(RepeatPattern.Weekly, thirdSunday.AddYears(CalendarService.MaxRepeatYears).AddDays(1))));
    }

    [Fact]
    public async Task ARepeatingEvent_CreatesOneEventPerDate_InOneSeries()
    {
        var first = Today.AddDays(1);
        var id = await Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(
            new EventInput(first, "Divine Liturgy", new TimeOnly(10, 0), null, null, ChapterEventKind.Service),
            new EventRepeat(RepeatPattern.Weekly, first.AddDays(21))));

        var detail = await Calendar(As(Parent, Role.Parent), s => s.GetEventAsync(id));
        Assert.Equal((ChapterEventKind.Service, id, 3), (detail!.Kind, detail.SeriesId, detail.LaterInSeries));
        using var db = CreateDb(TestSeedUserContext.Instance);
        Assert.Equal([first, first.AddDays(7), first.AddDays(14), first.AddDays(21)],
            db.ChapterEvents.Where(e => e.SeriesId == id).OrderBy(e => e.Date).Select(e => e.Date).ToList());
    }

    [Fact]
    public async Task OneClassOrExcursionADay_ButParishEventsAreNotLimited()
    {
        await Create(Today);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin),
            s => s.CreateEventAsync(new EventInput(Today, "Trip", null, null, null, ChapterEventKind.Excursion))));
        await Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(new EventInput(Today, "Vespers", null, null, null, ChapterEventKind.Service)));
        await Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(new EventInput(Today, "Patronal feast", null, null, null, ChapterEventKind.ParishMeeting)));
        // A series is refused whole when any of its dates already has a class or excursion.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(
            new EventInput(Today.AddDays(-7), "Class", null, null, null), new EventRepeat(RepeatPattern.Weekly, Today.AddDays(7)))));

        var day = (await Calendar(As(Parent, Role.Parent), s => s.GetMonthAsync(Today.Year, Today.Month))).Days.Single(d => d.Date == Today);
        Assert.Equal(3, day.Events.Count);
        using var db = CreateDb(TestSeedUserContext.Instance);
        Assert.DoesNotContain(db.ChapterEvents, e => e.Title == "Class");
    }

    [Fact]
    public async Task RemovingADateAndItsLaterDates_KeepsAnyWithARegister_AndEarlierDates()
    {
        var first = Today.AddDays(-14);
        var id = await Calendar(As(Admin, Role.Admin), s => s.CreateEventAsync(
            new EventInput(first, "Class", null, null, null), new EventRepeat(RepeatPattern.Weekly, first.AddDays(28))));
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            var withRegister = db.ChapterEvents.Single(e => e.SeriesId == id && e.Date == first.AddDays(14));
            db.AttendanceRegisters.Add(new AttendanceRegister { Id = 1, ChapterId = Chapter.ShrewsburyId, ChapterEventId = withRegister.Id, RankId = 1, MeetingDate = withRegister.Date, TakenByUserId = Teacher, TakenAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        int second;
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            second = db.ChapterEvents.Single(e => e.SeriesId == id && e.Date == first.AddDays(7)).Id;
        }

        var removal = await Calendar(As(Admin, Role.Admin), s => s.DeleteEventAsync(second, andLaterInSeries: true));

        Assert.Equal(new EventRemoval(3, 1), removal);
        using var after = CreateDb(TestSeedUserContext.Instance);
        Assert.Equal([first, first.AddDays(14)], after.ChapterEvents.Where(e => e.SeriesId == id).OrderBy(e => e.Date).Select(e => e.Date).ToList());
    }

    [Fact]
    public async Task AnEventsKind_CannotChange_OnceARegisterIsTaken()
    {
        var id = await Create(Today);
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            db.AttendanceRegisters.Add(new AttendanceRegister { Id = 1, ChapterId = Chapter.ShrewsburyId, ChapterEventId = id, RankId = 1, MeetingDate = Today, TakenByUserId = Teacher, TakenAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Calendar(As(Admin, Role.Admin),
            s => s.UpdateEventAsync(id, new EventInput(Today, "Now a trip", null, null, null, ChapterEventKind.Excursion))));
        await Calendar(As(Admin, Role.Admin), s => s.UpdateEventAsync(id, new EventInput(Today, "Renamed", null, null, null)));
    }
}
