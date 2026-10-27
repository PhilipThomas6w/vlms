using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Emails around the consent form (FR-003, adr/0008 decision 3, TC-035): the Admin and Safeguarding
/// Officer alert and parent confirmation on submission, and the parent's outcome email on approval
/// or rejection (the rejection comment is not emailed, adr/0008 decision 8). Covers recipients
/// (not-left staff only, the submitting parent only), no sensitive content, sent after the save, and a
/// failing send neither throwing nor undoing the action.
/// </summary>
public sealed class ConsentNotificationServiceTests : IDisposable
{
    private const string BaseUrl = "https://www.example.test";
    private const string Medical = "Severe asthma (sensitive)";
    private const string Dietary = "Peanut allergy (sensitive)";
    private const string Sen = "Needs extra time to read (sensitive)";
    private const string Emergency = "Grandma 07000 111222 (sensitive)";

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ConsentNotificationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-consent-notify-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(1, Role.Admin);
        schema.Context.Database.EnsureCreated();
        Seed(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class Caller(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext user) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ListLogger<ConsentNotificationService> Logger { get; } = new();

        public ConsentNotificationService Notifier(INotificationService notifications) =>
            new(Context, notifications, Logger, BaseUrl);

        public ParentConsentService Parent(INotificationService notifications) => new(Context, user, Notifier(notifications));
        public ConsentRecordService Records(INotificationService notifications) => new(Context, user, Notifier(notifications));

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Caller CreateContext(int userId, params Role[] roles)
    {
        var user = new FakeCurrentUserContext(userId, roles);
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(user);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new Caller(provider, scope, scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), user);
    }

    private static void Seed(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 2, DisplayName = "Safeguarding One", Email = "sg@example.com" });
        context.AppUsers.Add(new AppUser { Id = 3, DisplayName = "Admin Left", Email = "adminleft@example.com", HasLeft = true, LeftAt = DateTime.UtcNow.AddDays(-1) });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher@example.com" });
        context.AppUsers.Add(new AppUser { Id = 30, DisplayName = "Jane Parent", Email = "jane@example.com" });
        context.AppUsers.Add(new AppUser { Id = 31, DisplayName = "Pat Parent", Email = "pat@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin });
        context.UserRoles.Add(new UserRole { UserId = 2, Role = Role.SafeguardingOfficer });
        context.UserRoles.Add(new UserRole { UserId = 3, Role = Role.Admin });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.UserRoles.Add(new UserRole { UserId = 30, Role = Role.Parent });
        context.UserRoles.Add(new UserRole { UserId = 31, Role = Role.Parent });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "x", IsPrimary = true, AppUserId = 30 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Pat Parent", ContactInfo = "x", IsPrimary = true, AppUserId = 31 });
        context.Students.Add(new Student
        {
            Id = 100, Name = "Alex Student", DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 1, CreatedByUserId = 1 });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 2, CreatedByUserId = 1 });
        context.SaveChanges();
    }

    private static ParentConsentForm Form() =>
        new("Alex Student", new DateOnly(2012, 1, 1), true, false, false, true, Emergency, Medical, Dietary, Sen);

    private static void AssertNoSensitiveContent(IEnumerable<NotificationRequest> requests)
    {
        foreach (var r in requests)
        {
            var text = r.Subject + r.Body;
            Assert.DoesNotContain("asthma", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Peanut", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("extra time", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Grandma", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("07000", text);
        }
    }

    private async Task<int> SubmitAsync(INotificationService notifications)
    {
        using var parent = CreateContext(30, Role.Parent);
        return (await parent.Parent(notifications).SubmitAsync(100, Form())).Id;
    }

    [Fact]
    public async Task Submit_AlertsActiveAdminsAndSafeguardingOfficers_AndConfirmsToParent_WithoutSensitiveContent()
    {
        var fake = new FakeNotificationService();

        var recordId = await SubmitAsync(fake);

        var staff = fake.SentRequests.Where(r => r.RecipientEmail is "admin@example.com" or "sg@example.com").ToList();
        Assert.Equal(2, staff.Count);
        Assert.All(staff, r =>
        {
            Assert.Equal(NotificationPriority.Standard, r.Priority);
            Assert.Contains("Alex Student", r.Subject);
            Assert.Contains($"{BaseUrl}/safeguarding/consent", r.Body);
        });
        Assert.DoesNotContain(fake.SentRequests, r => r.RecipientEmail == "adminleft@example.com");
        Assert.DoesNotContain(fake.SentRequests, r => r.RecipientEmail is "teacher@example.com" or "pat@example.com");

        var parent = Assert.Single(fake.SentRequests, r => r.RecipientEmail == "jane@example.com");
        Assert.Contains("Alex Student", parent.Subject);
        Assert.Equal(3, fake.SentRequests.Count);
        AssertNoSensitiveContent(fake.SentRequests);
        Assert.True(recordId > 0);
    }

    [Fact]
    public async Task Submit_SendsOnlyAfterTheRecordIsSaved()
    {
        var sawRecord = new List<bool>();
        var checking = new CallbackNotificationService(() =>
        {
            using var verify = CreateContext(1, Role.Admin);
            sawRecord.Add(verify.Context.ConsentRecords.Any(c => c.Status == ConsentStatus.Pending));
        });

        await SubmitAsync(checking);

        Assert.NotEmpty(sawRecord);
        Assert.All(sawRecord, Assert.True);
    }

    [Fact]
    public async Task Submit_WhenSendThrows_StillSavesTheRecord_DoesNotThrow_AndLogs()
    {
        var failing = new CallbackNotificationService(() => throw new InvalidOperationException("mail down"));
        using var parent = CreateContext(30, Role.Parent);

        var record = await parent.Parent(failing).SubmitAsync(100, Form());

        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(ConsentStatus.Pending, verify.Context.ConsentRecords.Single(c => c.Id == record.Id).Status);
        Assert.Contains(parent.Logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task Approve_EmailsOnlyTheSubmittingParent_WithExpiryDate()
    {
        var recordId = await SubmitAsync(new FakeNotificationService());
        var fake = new FakeNotificationService();

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        var record = await sg.Records(fake).DecideAsync(recordId, approve: true);

        var mail = Assert.Single(fake.SentRequests);
        Assert.Equal("jane@example.com", mail.RecipientEmail);
        Assert.Equal(NotificationPriority.Standard, mail.Priority);
        Assert.Contains("approved", mail.Subject);
        Assert.Contains($"{record.ExpiryDate:dd/MM/yyyy}", mail.Body);
        AssertNoSensitiveContent(fake.SentRequests);
    }

    [Fact]
    public async Task Reject_EmailsParentWithSignInPointerAndFormLink_ButNotTheReviewerComments()
    {
        const string comment = "Please add a second emergency contact.";
        var recordId = await SubmitAsync(new FakeNotificationService());
        var fake = new FakeNotificationService();

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        await sg.Records(fake).DecideAsync(recordId, approve: false, comments: comment);

        var mail = Assert.Single(fake.SentRequests);
        Assert.Equal("jane@example.com", mail.RecipientEmail);
        Assert.Contains("needs changes", mail.Subject);
        Assert.DoesNotContain(comment, mail.Subject + mail.Body);
        Assert.DoesNotContain("second emergency contact", mail.Subject + mail.Body);
        Assert.Contains("Please sign in to read the comments from the reviewer.", mail.Body);
        Assert.Contains($"{BaseUrl}/parent/consent", mail.Body);
        AssertNoSensitiveContent(fake.SentRequests);

        // The comment itself is still kept on the record, where the parent's consent page reads it after sign-in.
        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(comment, verify.Context.ConsentRecords.Single(c => c.Id == recordId).DecisionComments);
    }

    [Fact]
    public async Task Decide_WhenSendThrows_StillSavesTheDecision_AndDoesNotThrow()
    {
        var recordId = await SubmitAsync(new FakeNotificationService());
        var failing = new CallbackNotificationService(() => throw new InvalidOperationException("mail down"));

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        await sg.Records(failing).DecideAsync(recordId, approve: true);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(ConsentStatus.Approved, verify.Context.ConsentRecords.Single(c => c.Id == recordId).Status);
    }

    [Fact]
    public async Task StaffRecordedForm_SendsNoSubmissionAlert()
    {
        var fake = new FakeNotificationService();
        using var admin = CreateContext(1, Role.Admin);

        await admin.Records(fake).RecordAsync(100, new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), new DateOnly(2027, 8, 31),
            true, true, true, true, 1, "Emergency", null, null, null);

        Assert.Empty(fake.SentRequests);
    }

    [Fact]
    public async Task CountPending_CountsPendingOnly_AndIsRestrictedToAdminOrSafeguardingOfficer()
    {
        var recordId = await SubmitAsync(new FakeNotificationService());

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        Assert.Equal(1, await sg.Records(new FakeNotificationService()).CountPendingAsync());
        await sg.Records(new FakeNotificationService()).DecideAsync(recordId, approve: true);
        Assert.Equal(0, await sg.Records(new FakeNotificationService()).CountPendingAsync());

        using var teacher = CreateContext(10, Role.Teacher);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => teacher.Records(new FakeNotificationService()).CountPendingAsync());
    }

    private sealed class CallbackNotificationService(Action onSend) : INotificationService
    {
        public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default)
        {
            onSend();
            return Task.FromResult(NotificationOutcome.Sent);
        }
    }
}
