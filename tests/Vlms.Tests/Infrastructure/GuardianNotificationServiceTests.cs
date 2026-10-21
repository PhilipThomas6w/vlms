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
            PhotoMediaConsent = true, TransportOffsiteConsent = true, DataSharingConsent = true,
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

    [Theory]
    [InlineData(28)]
    [InlineData(7)]
    [InlineData(1)]
    public async Task Reminder_SentOnScheduledDays(int daysLeft)
    {
        var today = new DateOnly(2026, 10, 20);
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(
            Result(new ConsentExpiryFlag(100, "Alex Student", 1, today.AddDays(daysLeft), IsExpired: false)), today);

        var sent = Assert.Single(notifications.SentRequests);
        Assert.Equal("approved@example.com", sent.RecipientEmail);
        Assert.Equal(NotificationPriority.Standard, sent.Priority);
        Assert.Contains(today.AddDays(daysLeft).ToString("dd/MM/yyyy"), sent.Body);
    }

    [Theory]
    [InlineData(27, false)]
    [InlineData(10, false)]
    [InlineData(0, false)]
    [InlineData(-5, true)]
    public async Task Reminder_NotSentOnOtherDays(int daysLeft, bool isExpired)
    {
        var today = new DateOnly(2026, 10, 20);
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(
            Result(new ConsentExpiryFlag(100, "Alex Student", 1, today.AddDays(daysLeft), isExpired)), today);

        Assert.Empty(notifications.SentRequests);
    }

    [Fact]
    public async Task LapsedConsent_DayAfterExpiry_SendsSafeguardingCriticalNotice()
    {
        var today = new DateOnly(2026, 10, 20);
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(
            Result(new ConsentExpiryFlag(100, "Alex Student", 1, today.AddDays(-1), IsExpired: true)), today);

        var sent = Assert.Single(notifications.SentRequests);
        Assert.Equal(NotificationPriority.SafeguardingCritical, sent.Priority);
        Assert.Contains("has expired", sent.Subject);
    }

    [Fact]
    public async Task NoConsentRecordAtAll_SendsNothing()
    {
        var notifications = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Notifier(notifications).SendConsentRemindersAsync(
            Result(new ConsentExpiryFlag(100, "Alex Student", null, null, IsExpired: true)), new DateOnly(2026, 10, 20));

        Assert.Empty(notifications.SentRequests);
    }

    [Fact]
    public async Task Reminders_RequireAdminOrSafeguardingOfficer()
    {
        using var teacher = CreateContext(10, Role.Teacher);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            teacher.Notifier(new FakeNotificationService()).SendConsentRemindersAsync(Result(), new DateOnly(2026, 10, 20)));
    }

    [Fact]
    public async Task Notifications_ReadNoRestrictedData()
    {
        var today = new DateOnly(2026, 10, 20);
        var notifications = new FakeNotificationService();
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await CompletionServiceFor(teacher, notifications).MarkCompleteAsync(100, 1);
        }

        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Notifier(notifications).SendConsentRemindersAsync(
                Result(new ConsentExpiryFlag(100, "Alex Student", 1, today.AddDays(7), IsExpired: false)), today);
        }

        Assert.Equal(2, notifications.SentRequests.Count);
        Assert.All(notifications.SentRequests, r => Assert.DoesNotContain(SensitiveMarker, r.Body));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.SensitiveDataAccessLogs.ToList());
    }

    private static ConsentExpiryJobResult Result(params ConsentExpiryFlag[] flags) =>
        new(flags, [], [], [], []);

    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("mail down");
    }
}
