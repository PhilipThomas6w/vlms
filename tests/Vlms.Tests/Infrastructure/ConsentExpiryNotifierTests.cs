using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="ConsentExpiryNotifier"/> — the class that translates a
/// <see cref="ConsentExpiryJobResult"/> into real safeguarding-critical notifications via
/// <see cref="INotificationService"/> (docs/design/low-level-design.md "NotificationService",
/// STATE.md). See its own doc comment for the documented scope decisions this test suite exercises:
/// only the two IsExpired=true flag categories are safeguarding-critical; one digest email per
/// Admin/SafeguardingOfficer recipient, not one per flag.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="ConsentExpiryJobTests"/> — needs real
/// <see cref="AppUser"/>/<see cref="UserRole"/> data to resolve recipients.
/// </summary>
public sealed class ConsentExpiryNotifierTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ConsentExpiryNotifierTests()
    {
        _connectionString = $"Data Source=file:vlms-notifier-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(SystemCurrentUserContext.Instance);
        schema.Context.Database.EnsureCreated();

        schema.Context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin1@example.com" });
        schema.Context.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin });

        schema.Context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "sgo-1", DisplayName = "Safeguarding One", Email = "sgo1@example.com" });
        schema.Context.UserRoles.Add(new UserRole { UserId = 2, Role = Role.SafeguardingOfficer });

        // A Teacher must never receive this escalation — it's Admin/SafeguardingOfficer only.
        schema.Context.AppUsers.Add(new AppUser { Id = 3, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        schema.Context.UserRoles.Add(new UserRole { UserId = 3, Role = Role.Teacher });

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

    [Fact]
    public async Task NotifyAsync_NoExpiredFlags_SendsNothing()
    {
        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var notificationService = new FakeNotificationService();
        var sut = new ConsentExpiryNotifier(run.Context, SystemCurrentUserContext.Instance, notificationService);

        var result = new ConsentExpiryJobResult(
            ConsentFlags: [new ConsentExpiryFlag(100, "Approaching Only", 1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), IsExpired: false)],
            DbsFlags: [],
            AtRiskStudents: [new AtRiskStudentFlag(200, "At Risk Only", DateTime.UtcNow.AddDays(-60), 60)]);

        var outcomes = await sut.NotifyAsync(result);

        Assert.Empty(outcomes);
        Assert.Empty(notificationService.SentRequests);
    }

    [Fact]
    public async Task NotifyAsync_ExpiredConsent_SendsOneDigestEmail_ToEachAdminAndSafeguardingOfficer_NotToTeacher()
    {
        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var notificationService = new FakeNotificationService();
        var sut = new ConsentExpiryNotifier(run.Context, SystemCurrentUserContext.Instance, notificationService);

        var result = new ConsentExpiryJobResult(
            ConsentFlags: [new ConsentExpiryFlag(100, "Expired Student", 1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5)), IsExpired: true)],
            DbsFlags: [],
            AtRiskStudents: []);

        var outcomes = await sut.NotifyAsync(result);

        Assert.Equal(2, outcomes.Count); // one per Admin/SafeguardingOfficer recipient
        Assert.Equal(2, notificationService.SentRequests.Count);
        Assert.Contains(notificationService.SentRequests, r => r.RecipientEmail == "admin1@example.com");
        Assert.Contains(notificationService.SentRequests, r => r.RecipientEmail == "sgo1@example.com");
        Assert.DoesNotContain(notificationService.SentRequests, r => r.RecipientEmail == "teacher1@example.com");
        Assert.All(notificationService.SentRequests, r => Assert.Equal(NotificationPriority.SafeguardingCritical, r.Priority));
        Assert.All(notificationService.SentRequests, r => Assert.Contains("Expired Student", r.Body));
    }

    [Fact]
    public async Task NotifyAsync_ExpiredDbs_IsAlsoSafeguardingCritical()
    {
        using var run = CreateContext(SystemCurrentUserContext.Instance);
        var notificationService = new FakeNotificationService();
        var sut = new ConsentExpiryNotifier(run.Context, SystemCurrentUserContext.Instance, notificationService);

        var result = new ConsentExpiryJobResult(
            ConsentFlags: [],
            DbsFlags: [new DbsExpiryFlag(3, "Teacher One", 1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5)), IsExpired: true)],
            AtRiskStudents: []);

        var outcomes = await sut.NotifyAsync(result);

        Assert.Equal(2, outcomes.Count);
        Assert.All(notificationService.SentRequests, r => Assert.Contains("Teacher One", r.Body));
    }

    [Fact]
    public async Task NotifyAsync_ByCallerWithoutAdminOrSafeguardingOfficerRole_Throws()
    {
        var noRoleCaller = new FakeCurrentUserContext(userId: null);
        using var run = CreateContext(noRoleCaller);
        var notificationService = new FakeNotificationService();
        var sut = new ConsentExpiryNotifier(run.Context, noRoleCaller, notificationService);

        var result = new ConsentExpiryJobResult([], [], []);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.NotifyAsync(result));
        Assert.Empty(notificationService.SentRequests);
    }
}
