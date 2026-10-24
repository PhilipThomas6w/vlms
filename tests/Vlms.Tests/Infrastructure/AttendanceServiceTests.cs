using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Attendance;
using Vlms.Infrastructure.Reporting;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the attendance registers (functional.md FR-020, FR-022, TC-052): one register per class
/// (rank) per meeting day, opened from the day's event; taking and correcting a register, who may take
/// and read it, that only the class's active students in the chapter can be marked and each only once a
/// day, the day's collated total with the caller's own classes first, the "attended without consent"
/// report and the Safeguarding Officer's email, the 12-month history, and the "two absences in a row"
/// at-risk rule.
/// </summary>
public sealed class AttendanceServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int Teacher = 10;
    private const int Admin = 11;
    private const int Officer = 12;
    private const int CoventryOfficer = 13;
    private const int CoventryTeacher = 14;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public AttendanceServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-attendance-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = CreateDb(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Guardsman" });
        AddUser(db, Teacher, "Teacher", (Role.Teacher, Chapter.ShrewsburyId));
        AddUser(db, Admin, "Admin", (Role.Admin, Chapter.ShrewsburyId));
        AddUser(db, Officer, "Officer", (Role.SafeguardingOfficer, Chapter.ShrewsburyId));
        AddUser(db, CoventryOfficer, "Coventry officer", (Role.SafeguardingOfficer, Coventry));
        AddUser(db, CoventryTeacher, "Coventry teacher", (Role.Teacher, Coventry));

        // Recruits: Alex has consent in force; Bea has none; Cal has left; Dee is in Coventry. Eve is a Guardsman.
        db.Students.Add(NewStudent(100, "Alex", StudentStatus.Active, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(200, "Bea", StudentStatus.Active, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(300, "Cal", StudentStatus.Inactive, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(400, "Dee", StudentStatus.Active, Coventry));
        db.Students.Add(NewStudent(500, "Eve", StudentStatus.Active, Chapter.ShrewsburyId, rankId: 2));
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, ChapterId = Chapter.ShrewsburyId, Name = "Parent", ContactInfo = "p@example.com", IsPrimary = true });
        db.ConsentRecords.Add(new ConsentRecord
        {
            Id = 1, StudentId = 100, PeriodStart = Today.AddMonths(-1), PeriodEnd = Today.AddMonths(11),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = 1, ExpiryDate = Today.AddMonths(11)
        });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext CreateDb(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private static Student NewStudent(int id, string name, StudentStatus status, int chapterId, int rankId = 1) => new()
    {
        Id = id, ChapterId = chapterId, Name = name, DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = rankId,
        Status = status, EnrolmentDate = Today.AddYears(-1)
    };

    private static void AddUser(VlmsDbContext db, int id, string name, params (Role Role, int Chapter)[] roles)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    private static FakeCurrentUserContext As(int userId, Role role, int chapterId = Chapter.ShrewsburyId) =>
        new(userId, role) { ActiveChapterId = chapterId };

    private async Task<T> Run<T>(ICurrentUserContext user, Func<AttendanceService, Task<T>> action)
    {
        using var db = CreateDb(user);
        return await action(new AttendanceService(db, user));
    }

    /// <summary>The chapter's event on <paramref name="date"/>, added if there is none yet.</summary>
    private int EventOn(DateOnly date, int chapterId = Chapter.ShrewsburyId)
    {
        using var db = CreateDb(TestSeedUserContext.Instance);
        var existing = db.ChapterEvents.Where(e => e.Date == date && e.ChapterId == chapterId).Select(e => (int?)e.Id).SingleOrDefault();
        if (existing is int id)
        {
            return id;
        }

        var next = db.ChapterEvents.Select(e => e.Id).AsEnumerable().DefaultIfEmpty(0).Max() + 1;
        db.ChapterEvents.Add(new ChapterEvent { Id = next, ChapterId = chapterId, Date = date, Title = "Chapter meeting", CreatedByUserId = Admin, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return next;
    }

    /// <summary>Takes the Recruit class's register at the meeting on <paramref name="date"/>.</summary>
    private Task<RegisterSaveResult> Save(DateOnly date, params (int Student, bool Present)[] marks) =>
        SaveClass(date, 1, marks);

    private Task<RegisterSaveResult> SaveClass(DateOnly date, int rankId, params (int Student, bool Present)[] marks)
    {
        var eventId = EventOn(date);
        return Run(As(Teacher, Role.Teacher), s => s.SaveRegisterAsync(eventId, rankId, marks.ToDictionary(m => m.Student, m => m.Present)));
    }

    private Task<RegisterView?> View(ICurrentUserContext user, DateOnly date, int rankId = 1)
    {
        var eventId = EventOn(date);
        return Run(user, s => s.GetRegisterAsync(eventId, rankId));
    }

    [Fact]
    public async Task Teacher_TakesTheRegister_AndItListsEveryActiveStudentInTheClassWithTheirMark()
    {
        var result = await Save(Today, (100, true), (200, false));

        Assert.Equal((1, 1), (result.Present, result.Absent));
        var view = await View(As(Teacher, Role.Teacher), Today);
        Assert.NotNull(view);
        Assert.True(view.Taken);
        Assert.Equal("Teacher", view.TakenByName);
        Assert.Equal("Recruit", view.RankName);
        Assert.Equal(["Alex", "Bea"], view.Students.Select(r => r.StudentName));
        Assert.Equal([true, false], view.Students.Select(r => r.Present));
        Assert.Equal([true, false], view.Students.Select(r => r.ConsentInForce));
    }

    [Fact]
    public async Task SavingAgain_CorrectsTheMarks_OnTheSameRegister()
    {
        await Save(Today, (100, true), (200, false));
        await Save(Today, (200, true));

        using var db = CreateDb(As(Teacher, Role.Teacher));
        var register = Assert.Single(db.AttendanceRegisters.Include(r => r.Marks).ToList());
        Assert.Equal([(100, true), (200, true)], register.Marks.OrderBy(m => m.StudentId).Select(m => (m.StudentId, m.Present)));
        Assert.Equal(Chapter.ShrewsburyId, register.ChapterId);
        Assert.All(register.Marks, m => Assert.Equal(Chapter.ShrewsburyId, m.ChapterId));
    }

    [Fact]
    public async Task EachClass_HasItsOwnRegister_AndTheDayIsCollated_WithTheCallersClassesFirst()
    {
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            db.ClassTeachers.Add(new ClassTeacher { Id = 1, RankId = 2, TeacherUserId = Teacher });
            db.SaveChanges();
        }

        await Save(Today, (100, true), (200, false));
        await SaveClass(Today, 2, (500, true));

        var guardsmen = await View(As(Teacher, Role.Teacher), Today, rankId: 2);
        Assert.Equal(["Eve"], guardsmen!.Students.Select(r => r.StudentName));
        var day = await Run(As(Teacher, Role.Teacher), s => s.GetEventRegistersAsync(EventOn(Today)));
        Assert.NotNull(day);
        Assert.Equal(["Guardsman", "Recruit"], day.Classes.Select(c => c.RankName));
        Assert.Equal([true, false], day.Classes.Select(c => c.IsMine));
        Assert.Equal(["Teacher"], day.Classes[0].Teachers);
        Assert.Equal((2, 1, 2), (day.Present, day.Absent, day.ClassesTaken));
        var listed = Assert.Single(await Run(As(Admin, Role.Admin), s => s.ListMeetingDaysAsync()));
        Assert.Equal((2, 1, 2), (listed.Present, listed.Absent, listed.ClassesTaken));
    }

    [Fact]
    public async Task AnyTeacher_TakesAnyClasssRegister_NotOnlyTheClasssOwnTeachers()
    {
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            AddUser(db, 15, "Other teacher", (Role.Teacher, Chapter.ShrewsburyId));
            db.ClassTeachers.Add(new ClassTeacher { Id = 1, RankId = 2, TeacherUserId = 15 });
            db.SaveChanges();
        }

        var result = await SaveClass(Today, 2, (500, true));

        Assert.Equal((1, 0), (result.Present, result.Absent));
        Assert.Equal("Teacher", (await View(As(Teacher, Role.Teacher), Today, rankId: 2))!.TakenByName);
    }

    [Fact]
    public async Task AStudentAlreadyOnAnotherClasssRegisterThatDay_IsLeftOff_AndCannotBeMarkedTwice()
    {
        await Save(Today, (100, true));
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            // Alex is promoted after the Recruit register was taken.
            db.Entry(db.Students.Single(s => s.Id == 100)).Property(s => s.CurrentRankId).CurrentValue = 2;
            db.SaveChanges();
        }

        var guardsmen = await View(As(Teacher, Role.Teacher), Today, rankId: 2);
        Assert.Equal(["Eve"], guardsmen!.Students.Select(r => r.StudentName));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveClass(Today, 2, (100, true)));
        Assert.Contains("Alex", error.Message);
        var recruits = await View(As(Teacher, Role.Teacher), Today);
        Assert.Contains(recruits!.Students, r => r.StudentName == "Alex" && r.Present == true);
    }

    [Fact]
    public async Task ARegister_NeedsAnEventInThisChaptersCalendar()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(As(Teacher, Role.Teacher), s => s.SaveRegisterAsync(999, 1, new Dictionary<int, bool> { [100] = true })));
        Assert.Null(await Run(As(Teacher, Role.Teacher), s => s.GetRegisterAsync(999, 1)));
        Assert.Null(await Run(As(Teacher, Role.Teacher), s => s.GetEventRegistersAsync(999)));
    }

    [Fact]
    public async Task AFutureMeeting_OrAnEmptyRegister_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today.AddDays(1), (100, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today));
        Assert.False((await Run(As(Teacher, Role.Teacher), s => s.GetEventRegistersAsync(EventOn(Today.AddDays(1)))))!.CanBeTaken);
    }

    [Theory]
    [InlineData(300)] // left
    [InlineData(400)] // another chapter's student
    [InlineData(999)] // unknown
    [InlineData(500)] // another class's student
    public async Task OnlyThisClasssActiveStudentsInThisChapter_CanBeMarked(int studentId)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today, (100, true), (studentId, true)));

        using var db = CreateDb(TestSeedUserContext.Instance);
        Assert.Empty(db.AttendanceMarks.ToList());
    }

    [Fact]
    public async Task AdminsRead_ButOnlyTeachersTake_AndOtherRolesAreRefused()
    {
        await Save(Today, (100, true));

        var eventId = EventOn(Today);
        Assert.True((await View(As(Admin, Role.Admin), Today))!.Taken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Run(As(Admin, Role.Admin), s => s.SaveRegisterAsync(eventId, 1, new Dictionary<int, bool> { [100] = true })));
        foreach (var role in new[] { Role.Parent, Role.Student, Role.Approver, Role.SafeguardingOfficer, Role.NationalAdministrator })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.GetRegisterAsync(eventId, 1)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.GetEventRegistersAsync(eventId)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.ListMeetingDaysAsync()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.GetStudentHistoryAsync(100)));
        }
    }

    [Fact]
    public async Task AnotherChaptersTeacher_DoesNotSeeOrTakeThisChaptersRegisters()
    {
        await Save(Today, (100, true));
        var shrewsburyEvent = EventOn(Today);
        var coventryEvent = EventOn(Today, Coventry);

        Assert.Null(await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetRegisterAsync(shrewsburyEvent, 1)));
        Assert.Null(await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetEventRegistersAsync(shrewsburyEvent)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(As(CoventryTeacher, Role.Teacher, Coventry),
            s => s.SaveRegisterAsync(shrewsburyEvent, 1, new Dictionary<int, bool> { [400] = true })));
        var view = await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetRegisterAsync(coventryEvent, 1));
        Assert.False(view!.Taken);
        Assert.Equal(["Dee"], view.Students.Select(r => r.StudentName));
        Assert.Equal([coventryEvent], (await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.ListMeetingDaysAsync())).Select(d => d.EventId));
    }

    [Fact]
    public async Task PresentWithoutConsent_ListsOnlyNewlyPresentChildrenWithoutConsent()
    {
        var first = await Save(Today, (100, true), (200, true));
        var again = await Save(Today, (100, true), (200, true));

        Assert.Equal([200], first.PresentWithoutConsent);
        Assert.Empty(again.PresentWithoutConsent);
    }

    [Fact]
    public async Task TheSafeguardingOfficersOfTheChapter_AreToldOnly_WithNoSensitiveDetails()
    {
        var notifications = new FakeNotificationService();
        var teacher = As(Teacher, Role.Teacher);
        using var db = CreateDb(teacher);
        var service = new AttendanceNotificationService(db, teacher, notifications,
            NullLogger<AttendanceNotificationService>.Instance, "https://example.test");

        await service.NotifyPresentWithoutConsentAsync(Today, []);
        Assert.Empty(notifications.SentRequests);

        await service.NotifyPresentWithoutConsentAsync(Today, [200]);
        var sent = Assert.Single(notifications.SentRequests);
        Assert.Equal("user12@example.com", sent.RecipientEmail);
        Assert.Contains("Bea", sent.Subject);
        Assert.Contains("https://example.test/safeguarding/consent", sent.Body);
        Assert.Equal(NotificationPriority.SafeguardingCritical, sent.Priority);
    }

    [Fact]
    public async Task GetMarks_ReturnsTheDaysMarksAcrossEveryClass_OrNothingWithoutARegister()
    {
        await Save(Today.AddDays(-1), (100, true), (200, false));
        await SaveClass(Today.AddDays(-1), 2, (500, true));

        var marks = await Run(As(Teacher, Role.Teacher), s => s.GetMarksAsync(Today.AddDays(-1)));
        Assert.Equal([(100, true), (200, false), (500, true)], marks.OrderBy(m => m.Key).Select(m => (m.Key, m.Value)));
        Assert.Empty(await Run(As(Teacher, Role.Teacher), s => s.GetMarksAsync(Today)));
    }

    [Fact]
    public async Task History_CoversTheLast12Months_NewestFirst_ForActiveStudentsOnly()
    {
        await Save(Today.AddMonths(-13), (100, true));
        await Save(Today.AddMonths(-2), (100, false));
        await Save(Today.AddDays(-3), (100, true));

        var history = await Run(As(Admin, Role.Admin), s => s.GetStudentHistoryAsync(100));

        Assert.NotNull(history);
        Assert.Equal([Today.AddDays(-3), Today.AddMonths(-2)], history.Entries.Select(e => e.MeetingDate));
        Assert.Equal((1, 2), (history.Attended, history.Sessions));
        Assert.Null(await Run(As(Admin, Role.Admin), s => s.GetStudentHistoryAsync(300)));
    }

    [Fact]
    public async Task TwoAbsencesInARow_FlagAStudentAtRisk_ButAnAbsenceThenAttendanceDoesNot()
    {
        // Alex: absent, absent (latest two). Bea: absent, then present.
        await Save(Today.AddDays(-60), (100, true), (200, false));
        await Save(Today.AddDays(-30), (100, false), (200, false));
        await Save(Today.AddDays(-1), (100, false), (200, true));
        using (var db = CreateDb(TestSeedUserContext.Instance))
        {
            // All completed a lesson recently, so only the absence rule can flag them.
            db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = Teacher, CompletedAt = DateTime.UtcNow, IsReversed = false });
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 2, StudentId = 200, LessonId = 1, CompletedByUserId = Teacher, CompletedAt = DateTime.UtcNow, IsReversed = false });
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 3, StudentId = 500, LessonId = 1, CompletedByUserId = Teacher, CompletedAt = DateTime.UtcNow, IsReversed = false });
            db.SaveChanges();
        }

        using var read = CreateDb(As(Admin, Role.Admin));
        var flags = await AtRiskStudentFlagging.GetAtRiskStudentsAsync(read, DateTime.UtcNow);

        var alex = Assert.Single(flags);
        Assert.Equal(100, alex.StudentId);
        Assert.True(alex.AbsentFromLastMeetings);
        Assert.False(alex.NoRecentCompletion);
    }
}
