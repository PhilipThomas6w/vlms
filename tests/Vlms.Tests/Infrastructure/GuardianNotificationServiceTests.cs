using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Progress;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Proactive parent notifications (BR-005, TC-015): completion and promotion emails through
/// <see cref="CompletionService"/>, consent-expiry reminders from the daily sweep's flags.
/// Covers recipient scoping (linked, approved, not-left guardians only), failure isolation (a
/// failing send never fails the completion), the reminder schedule, the role gates, and that no
/// safeguarding-restricted entity is read (no audit-log row written).
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="CompletionServiceTests"/>.
/// </summary>
public sealed class GuardianNotificationServiceTests : IDisposable
{
    private const string BaseUrl = "https://www.example.test";
    private const string SensitiveMarker = "Peanut allergy (sensitive)";

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public GuardianNotificationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-guardian-notify-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();
        Seed(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ICurrentUserContext CurrentUser { get; } = currentUser;
        public ListLogger<GuardianNotificationService> Logger { get; } = new();

        public GuardianNotificationService Notifier(INotificationService notifications, string? baseUrl = BaseUrl) =>
            new(Context, CurrentUser, notifications, Logger, baseUrl);

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(int? userId, params Role[] roles)
    {
        var currentUser = new FakeCurrentUserContext(userId, roles);
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new CallerContext(provider, scope, scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), currentUser);
    }

    private CompletionService CompletionServiceFor(CallerContext caller, INotificationService notifications) =>
        new(caller.Context, caller.CurrentUser, new PromotionService(caller.Context),
            new CertificateService(caller.Context, _blobStorage), guardianNotifications: caller.Notifier(notifications));

    private static void Seed(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Explorer" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", Order = 1, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "First Aid", Order = 1, IsActive = true });

        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Approved Parent", Email = "approved@example.com" });
        context.AppUsers.Add(new AppUser { Id = 21, DisplayName = "Waiting Parent", Email = "waiting@example.com" });
        context.AppUsers.Add(new AppUser { Id = 22, DisplayName = "Left Parent", Email = "left@example.com", HasLeft = true, LeftAt = DateTime.UtcNow.AddDays(-1) });
        context.AppUsers.Add(new AppUser { Id = 23, DisplayName = "Other Parent", Email = "other@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.UserRoles.Add(new UserRole { UserId = 20, Role = Role.Parent });
        context.UserRoles.Add(new UserRole { UserId = 22, Role = Role.Parent });
        context.UserRoles.Add(new UserRole { UserId = 23, Role = Role.Parent });

        // Guardians 1-4 are linked to student 100; guardian 1 has no account; guardian 5 (Other
        // Parent, approved) is linked to a different student only.
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "No Account", ContactInfo = "noaccount@example.com", IsPrimary = false });
        context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Approved Parent", ContactInfo = "x", IsPrimary = true, AppUserId = 20 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Waiting Parent", ContactInfo = "x", IsPrimary = false, AppUserId = 21 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 4, Name = "Left Parent", ContactInfo = "x", IsPrimary = false, AppUserId = 22 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 5, Name = "Other Parent", ContactInfo = "x", IsPrimary = true, AppUserId = 23 });

        foreach (var (id, name) in new[] { (100, "Alex Student"), (101, "Sam Student") })
        {
            context.Students.Add(new Student
            {
                Id = id, Name = name, DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
            });
            context.StudentRankProgresses.Add(new StudentRankProgress
            {
                Id = id, StudentId = id, RankId = 1, StartedAt = DateTime.UtcNow.AddDays(-30), CompletedAt = null
            });
        }

        foreach (var guardianId in new[] { 1, 2, 3, 4 })
        {
            context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = guardianId, CreatedByUserId = 10 });
        }

        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 101, ParentGuardianId = 5, CreatedByUserId = 10 });

        context.ConsentRecords.Add(new ConsentRecord
        {
            Id = 1, StudentId = 100, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = 2,
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6))
        });
        context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = 1, ConsentRecordId = 1, EmergencyMedicalInfo = SensitiveMarker, EmergencyContact = SensitiveMarker
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task Completion_EmailsOnlyLinkedApprovedNotLeftGuardians()
    {
        var notifications = new FakeNotificationService();
        using var teacher = CreateContext(10, Role.Teacher);

        await CompletionServiceFor(teacher, notifications).MarkCompleteAsync(100, 1);

        var sent = Assert.Single(notifications.SentRequests);
        Assert.Equal("approved@example.com", sent.RecipientEmail);
        Assert.Equal(NotificationPriority.Standard, sent.Priority);
        Assert.Contains("Alex Student", sent.Subject);
        Assert.Contains("Knots 101", sent.Body);
        Assert.Contains($"{BaseUrl}/parent/dashboard", sent.Body);
    }

    [Fact]
    public async Task CompletingTheRank_AlsoEmailsThePromotion()
    {
        var notifications = new FakeNotificationService();
        using var teacher = CreateContext(10, Role.Teacher);
        var service = CompletionServiceFor(teacher, notifications);

        await service.MarkCompleteAsync(100, 1);
        await service.MarkCompleteAsync(100, 2);

        Assert.Equal(3, notifications.SentRequests.Count);
        Assert.Contains("promoted to Explorer", notifications.SentRequests[^1].Subject);
        Assert.All(notifications.SentRequests, r => Assert.Equal("approved@example.com", r.RecipientEmail));
    }

    [Fact]
    public async Task FailingSend_DoesNotFailOrUndoTheCompletion()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var service = CompletionServiceFor(teacher, new ThrowingNotificationService());

        var completion = await service.MarkCompleteAsync(100, 1);

        using var verify = CreateContext(1, Role.Admin);
        Assert.True(await verify.Context.StudentLessonCompletions.AnyAsync(c => c.Id == completion.Id && !c.IsReversed));
        Assert.True(await verify.Context.Certificates.AnyAsync());
    }

    [Fact]
    public async Task FailingSend_IsLoggedAsWarning()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var notifier = teacher.Notifier(new ThrowingNotificationService());
        var completion = await new CompletionService(teacher.Context, teacher.CurrentUser,
            new PromotionService(teacher.Context), new CertificateService(teacher.Context, _blobStorage)).MarkCompleteAsync(100, 1);

        var outcomes = await notifier.NotifyCompletionAsync(completion.Id, promoted: false);

        Assert.Empty(outcomes);
        Assert.Contains(teacher.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("the completion stands"));
    }

    [Fact]
    public async Task NoBaseUrl_OmitsTheLink()
    {
        var notifications = new FakeNotificationService();
        using var teacher = CreateContext(10, Role.Teacher);
        var completion = await new CompletionService(teacher.Context, teacher.CurrentUser,
            new PromotionService(teacher.Context), new CertificateService(teacher.Context, _blobStorage)).MarkCompleteAsync(100, 1);

        await teacher.Notifier(notifications, baseUrl: null).NotifyCompletionAsync(completion.Id, promoted: false);

        Assert.DoesNotContain("http", Assert.Single(notifications.SentRequests).Body);
    }

    [Fact]
    public async Task NotifyCompletion_RequiresTeacher()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            admin.Notifier(new FakeNotificationService()).NotifyCompletionAsync(1, promoted: false));
    }

    // 31 August 2027 is a Tuesday. Student 100 (guardian "Approved Parent", approved@example.com) is the
    // one under test; student 101 (Other Parent) has no consent, so it gets the weekly email on
    // Mondays, which these tests filter out by recipient where it would otherwise get in the way.
    private static readonly DateOnly YearEnd = new(2027, 8, 31);

    private void SetConsents(params ConsentRecord[] records)
    {
        using var caller = CreateContext(1, Role.Admin);
        caller.Context.ConsentSensitiveDetails.RemoveRange(caller.Context.ConsentSensitiveDetails.IgnoreQueryFilters());
        caller.Context.ConsentRecords.RemoveRange(caller.Context.ConsentRecords);
        caller.Context.SaveChanges();
        caller.Context.ConsentRecords.AddRange(records);
        caller.Context.SaveChanges();
    }

    private static ConsentRecord Record(int id, ConsentStatus status, DateOnly expiry) => new()
    {
        Id = id, StudentId = 100, PeriodStart = expiry.AddYears(-1).AddDays(1), PeriodEnd = expiry,
        PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
        Status = status, SubmittedByParentId = 2, ExpiryDate = expiry
    };

    private async Task<List<NotificationRequest>> RemindersForAsync(DateOnly today, string recipient = "approved@example.com")
    {
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);
        await admin.Notifier(notifications).SendConsentRemindersAsync(today);
        return notifications.SentRequests.Where(r => r.RecipientEmail == recipient).ToList();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(14)]
    [InlineData(7)]
    [InlineData(1)]
    public async Task Renewal_SentOn30_14_7_And1DaysBefore31August(int daysLeft)
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));

        var sent = Assert.Single(await RemindersForAsync(YearEnd.AddDays(-daysLeft)));

        Assert.Equal(NotificationPriority.Standard, sent.Priority);
        Assert.Contains("31/08/2027", sent.Body);
        Assert.Contains("Alex Student", sent.Subject);
        Assert.Contains($"{BaseUrl}/parent/consent", sent.Body);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(29)]
    [InlineData(15)]
    [InlineData(10)]
    [InlineData(2)]
    [InlineData(0)]
    public async Task Renewal_NotSentOnOtherDays(int daysLeft)
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));

        // 28 days before is no longer a reminder day; 0 days is the last day of consent (a Tuesday).
        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(-daysLeft)));
    }

    [Fact]
    public async Task Renewal_NotSentWhenNewerApprovedConsentExists()
    {
        SetConsents(
            Record(1, ConsentStatus.Approved, YearEnd),
            Record(2, ConsentStatus.Approved, YearEnd.AddYears(1)));

        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(-7)));
    }

    [Fact]
    public async Task Renewal_NotSentWhileResubmissionAwaitsReview()
    {
        SetConsents(
            Record(1, ConsentStatus.Approved, YearEnd),
            Record(2, ConsentStatus.Pending, YearEnd.AddYears(1)));

        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(-7)));
    }

    [Fact]
    public async Task Renewal_NotSentToParentWhoHasLeft_ButOtherParentsStillGetIt()
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(YearEnd.AddDays(-14));

        Assert.DoesNotContain(notifications.SentRequests, r => r.RecipientEmail == "left@example.com");
        Assert.DoesNotContain(notifications.SentRequests, r => r.RecipientEmail == "waiting@example.com");
        Assert.Contains(notifications.SentRequests, r => r.RecipientEmail == "approved@example.com");
    }

    [Theory]
    [InlineData(StudentStatus.Inactive)]
    [InlineData(StudentStatus.Graduated)]
    public async Task NoReminders_ForStudentWhoIsNoLongerActive(StudentStatus status)
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));
        using (var admin = CreateContext(1, Role.Admin))
        {
            admin.Context.Entry(await admin.Context.Students.SingleAsync(s => s.Id == 100))
                .CurrentValues.SetValues(new { Status = status });
            await admin.Context.SaveChangesAsync();
        }

        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(-7)));
        Assert.Empty(await RemindersForAsync(new DateOnly(2027, 9, 6))); // a Monday
    }

    [Fact]
    public async Task NoReminders_ForStudentMarkedAsLeft()
    {
        SetConsents();
        using (var admin = CreateContext(1, Role.Admin))
        {
            admin.Context.Entry(await admin.Context.Students.SingleAsync(s => s.Id == 100))
                .CurrentValues.SetValues(new { LeftAt = DateTime.UtcNow });
            await admin.Context.SaveChangesAsync();
        }

        Assert.Empty(await RemindersForAsync(new DateOnly(2027, 9, 6)));
    }

    [Fact]
    public async Task Weekly_SentOnMondayWhileNoConsentInForce()
    {
        // Lapsed yesterday (31 August 2027); the first Monday after is 6 September 2027.
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));
        var monday = new DateOnly(2027, 9, 6);
        Assert.Equal(GuardianNotificationService.WeeklyReminderDay, monday.DayOfWeek);

        var sent = Assert.Single(await RemindersForAsync(monday));

        Assert.Contains("needs a consent form", sent.Subject);
        Assert.Contains($"{BaseUrl}/parent/consent", sent.Body);
    }

    [Fact]
    public async Task Lapse_DayAfterExpiry_SendsSafeguardingCriticalNotice_NotOnTheDayItself_NorTwoDaysAfter()
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));

        Assert.Empty(await RemindersForAsync(YearEnd)); // last valid day

        var sent = Assert.Single(await RemindersForAsync(YearEnd.AddDays(1))); // Wednesday 1 September 2027
        Assert.NotEqual(DayOfWeek.Monday, YearEnd.AddDays(1).DayOfWeek);
        Assert.Equal(NotificationPriority.SafeguardingCritical, sent.Priority);
        Assert.Contains("has expired", sent.Subject);
        Assert.Contains("31/08/2027", sent.Body);
        Assert.Contains($"{BaseUrl}/parent/consent", sent.Body);

        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(2))); // Thursday: not a weekly day
    }

    [Fact]
    public async Task Lapse_DayAfterExpiryOnAMonday_SendsOnlyTheLapseNotice_ThenWeeklyFollows()
    {
        var sunday = new DateOnly(2027, 9, 5);
        SetConsents(Record(1, ConsentStatus.Approved, sunday));
        var monday = sunday.AddDays(1);
        Assert.Equal(GuardianNotificationService.WeeklyReminderDay, monday.DayOfWeek);

        var sent = Assert.Single(await RemindersForAsync(monday));
        Assert.Equal(NotificationPriority.SafeguardingCritical, sent.Priority);
        Assert.Contains("has expired", sent.Subject);

        var nextWeek = Assert.Single(await RemindersForAsync(monday.AddDays(7)));
        Assert.Equal(NotificationPriority.Standard, nextWeek.Priority);
        Assert.Contains("needs a consent form", nextWeek.Subject);
    }

    [Fact]
    public async Task Lapse_NoticeNotSentWhileFormAwaitsReview_OrWhenNewerConsentInForce()
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd), Record(2, ConsentStatus.Pending, YearEnd.AddYears(1)));
        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(1)));

        SetConsents(Record(1, ConsentStatus.Approved, YearEnd), Record(2, ConsentStatus.Approved, YearEnd.AddYears(1)));
        Assert.Empty(await RemindersForAsync(YearEnd.AddDays(1)));
    }

    [Fact]
    public async Task Weekly_SentEveryMondayAndOnlyMondays()
    {
        SetConsents(); // never had consent
        var monday = new DateOnly(2027, 9, 6);

        for (var offset = 0; offset < 21; offset++)
        {
            var day = monday.AddDays(offset);
            var sent = await RemindersForAsync(day);
            if (day.DayOfWeek == DayOfWeek.Monday)
            {
                Assert.Single(sent);
            }
            else
            {
                Assert.Empty(sent);
            }
        }
    }

    [Fact]
    public async Task Weekly_SentWhileRejectedFormAwaitsResubmission_WithRejectionWording()
    {
        SetConsents(Record(1, ConsentStatus.Rejected, YearEnd));
        var monday = new DateOnly(2027, 9, 6);

        var sent = Assert.Single(await RemindersForAsync(monday));

        Assert.Contains("needs changes", sent.Subject);
    }

    [Fact]
    public async Task Weekly_RejectedRenewalReplacesTheRenewalEmailOnThatDay()
    {
        // Approved to 31/08/2027, and a newer form was rejected: on the Monday 7 days before expiry
        // the parent gets one weekly "needs changes" email, not that plus a renewal email.
        SetConsents(
            Record(1, ConsentStatus.Approved, YearEnd),
            Record(2, ConsentStatus.Rejected, YearEnd.AddYears(1)));
        var monday = new DateOnly(2027, 8, 23);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        var sent = Assert.Single(await RemindersForAsync(monday));

        Assert.Contains("needs changes", sent.Subject);
    }

    [Fact]
    public async Task Weekly_NotSentWhileFormAwaitsReview()
    {
        SetConsents(Record(1, ConsentStatus.Pending, YearEnd));

        Assert.Empty(await RemindersForAsync(new DateOnly(2027, 9, 6)));
    }

    [Fact]
    public async Task Weekly_NotSentWhenConsentInForce()
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd.AddYears(1)));

        Assert.Empty(await RemindersForAsync(new DateOnly(2027, 9, 6)));
    }

    [Fact]
    public async Task Weekly_NotSentToParentWhoHasLeft()
    {
        SetConsents();
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(new DateOnly(2027, 9, 6));

        Assert.DoesNotContain(notifications.SentRequests, r => r.RecipientEmail == "left@example.com");
        Assert.Contains(notifications.SentRequests, r => r.RecipientEmail == "approved@example.com");
    }

    [Fact]
    public async Task Reminders_DoNotIncludeSensitiveDetails()
    {
        SetConsents(Record(1, ConsentStatus.Approved, YearEnd));

        var sent = await RemindersForAsync(YearEnd.AddDays(-7));

        Assert.All(sent, r => Assert.DoesNotContain(SensitiveMarker, r.Body));
    }

    [Fact]
    public async Task FailingSend_DoesNotStopOtherStudentsReminders()
    {
        SetConsents();
        var notifications = new FailFirstNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(new DateOnly(2027, 9, 6));

        // Two students with a linked, active parent each; the first send throws, the second still goes.
        Assert.Equal(1, notifications.Delivered);
        Assert.Contains(admin.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("continuing"));
    }

    [Fact]
    public async Task Reminders_RequireAdminOrSafeguardingOfficer()
    {
        using var teacher = CreateContext(10, Role.Teacher);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teacher.Notifier(new FakeNotificationService()).SendConsentRemindersAsync(new DateOnly(2027, 9, 6)));
    }

    [Fact]
    public async Task Notifications_ReadNoRestrictedData()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)).AddDays(-7);
        var notifications = new FakeNotificationService();
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await CompletionServiceFor(teacher, notifications).MarkCompleteAsync(100, 1);
        }

        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Notifier(notifications).SendConsentRemindersAsync(today);
        }

        // Completion email plus the renewal reminder (and a weekly one for student 101 on a Monday).
        Assert.True(notifications.SentRequests.Count >= 2);
        Assert.All(notifications.SentRequests, r => Assert.DoesNotContain(SensitiveMarker, r.Body));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.SensitiveDataAccessLogs.ToList());
    }

    private sealed class FailFirstNotificationService : INotificationService
    {
        private bool _first = true;
        public int Delivered { get; private set; }

        public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("mail down");
            }

            Delivered++;
            return Task.FromResult(NotificationOutcome.Sent);
        }
    }

    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("mail down");
    }
}
