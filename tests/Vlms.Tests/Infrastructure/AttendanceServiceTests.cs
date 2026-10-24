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
/// Verifies the attendance register (functional.md FR-020, TC-052): taking and correcting a register,
/// who may take and read it, that only the chapter's active students can be marked, the
/// "attended without consent" report and the Safeguarding Officer's email, the 12-month history,
/// and the "two absences in a row" at-risk rule.
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
        AddUser(db, Teacher, "Teacher", (Role.Teacher, Chapter.ShrewsburyId));
        AddUser(db, Admin, "Admin", (Role.Admin, Chapter.ShrewsburyId));
        AddUser(db, Officer, "Officer", (Role.SafeguardingOfficer, Chapter.ShrewsburyId));
        AddUser(db, CoventryOfficer, "Coventry officer", (Role.SafeguardingOfficer, Coventry));
        AddUser(db, CoventryTeacher, "Coventry teacher", (Role.Teacher, Coventry));

        // Alex has consent in force; Bea has none; Cal has left; Dee is in Coventry.
        db.Students.Add(NewStudent(100, "Alex", StudentStatus.Active, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(200, "Bea", StudentStatus.Active, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(300, "Cal", StudentStatus.Inactive, Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(400, "Dee", StudentStatus.Active, Coventry));
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

    private static Student NewStudent(int id, string name, StudentStatus status, int chapterId) => new()
    {
        Id = id, ChapterId = chapterId, Name = name, DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
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

    private Task<RegisterSaveResult> Save(DateOnly date, params (int Student, bool Present)[] marks) =>
        Run(As(Teacher, Role.Teacher), s => s.SaveRegisterAsync(date, marks.ToDictionary(m => m.Student, m => m.Present)));

    [Fact]
    public async Task Teacher_TakesTheRegister_AndItListsEveryActiveStudentWithTheirMark()
    {
        var result = await Save(Today, (100, true), (200, false));

        Assert.Equal((1, 1), (result.Present, result.Absent));
        var view = await Run(As(Teacher, Role.Teacher), s => s.GetRegisterAsync(Today));
        Assert.True(view.Taken);
        Assert.Equal("Teacher", view.TakenByName);
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
        Assert.All(register.Marks, m => Assert.Equal(Chapter.ShrewsburyId, m.ChapterId));
    }

    [Fact]
    public async Task AFutureMeeting_OrAnEmptyRegister_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today.AddDays(1), (100, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today));
    }

    [Theory]
    [InlineData(300)] // left
    [InlineData(400)] // another chapter's student
    [InlineData(999)] // unknown
    public async Task OnlyThisChaptersActiveStudents_CanBeMarked(int studentId)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(Today, (100, true), (studentId, true)));

        using var db = CreateDb(TestSeedUserContext.Instance);
        Assert.Empty(db.AttendanceMarks.ToList());
    }

    [Fact]
    public async Task AdminsRead_ButOnlyTeachersTake_AndOtherRolesAreRefused()
    {
        await Save(Today, (100, true));

        Assert.True((await Run(As(Admin, Role.Admin), s => s.GetRegisterAsync(Today))).Taken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Run(As(Admin, Role.Admin), s => s.SaveRegisterAsync(Today, new Dictionary<int, bool> { [100] = true })));
        foreach (var role in new[] { Role.Parent, Role.Student, Role.Approver, Role.SafeguardingOfficer, Role.NationalAdministrator })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.GetRegisterAsync(Today)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(As(Officer, role), s => s.GetStudentHistoryAsync(100)));
        }
    }

    [Fact]
    public async Task AnotherChaptersTeacher_DoesNotSeeThisChaptersRegister()
    {
        await Save(Today, (100, true));

        var view = await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.GetRegisterAsync(Today));

        Assert.False(view.Taken);
        Assert.Equal(["Dee"], view.Students.Select(r => r.StudentName));
        Assert.Empty(await Run(As(CoventryTeacher, Role.Teacher, Coventry), s => s.ListRegistersAsync()));
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
    public async Task GetMarks_ReturnsTheMeetingsMarks_OrNothingWithoutARegister()
    {
        await Save(Today.AddDays(-1), (100, true), (200, false));

        var marks = await Run(As(Teacher, Role.Teacher), s => s.GetMarksAsync(Today.AddDays(-1)));
        Assert.Equal([(100, true), (200, false)], marks.OrderBy(m => m.Key).Select(m => (m.Key, m.Value)));
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
            // Both completed a lesson recently, so only the absence rule can flag them.
            db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = Teacher, CompletedAt = DateTime.UtcNow, IsReversed = false });
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 2, StudentId = 200, LessonId = 1, CompletedByUserId = Teacher, CompletedAt = DateTime.UtcNow, IsReversed = false });
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
