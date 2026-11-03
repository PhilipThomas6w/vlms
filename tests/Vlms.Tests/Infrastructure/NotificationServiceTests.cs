using Vlms.Domain;
using Vlms.Infrastructure.Notifications;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="NotificationService"/>'s retry/escalation logic (docs/design/
/// low-level-design.md "NotificationService" failure-handling paragraph, quality/test-plan.md
/// TC-013, STATE.md): a failed <see cref="NotificationPriority.SafeguardingCritical"/> send is
/// retried up to a bounded number of attempts and, if every attempt fails, logged as an
/// escalation-visible (<c>LogError</c>) failure and reported as
/// <see cref="NotificationOutcome.FailedAndEscalated"/> — never an exception. A
/// <see cref="NotificationPriority.Standard"/> send gets exactly one attempt; failure is logged at
/// <c>LogWarning</c> only (<see cref="NotificationOutcome.FailedLoggedOnly"/>), no escalation.
///
/// Uses <see cref="FakeEmailSender"/> (a hand-rolled fake, no mocking library, same spirit as
/// <see cref="InMemoryBlobStorage"/>/<see cref="ListLogger{T}"/> elsewhere in this test project) and
/// a no-op delay function so retry backoff doesn't actually make these tests wait.
/// </summary>
public sealed class NotificationServiceTests
{
    private static Task NoOpDelay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;

    private static NotificationRequest Request(NotificationPriority priority) =>
        new("recipient@example.com", "Recipient Name", "Test subject", "Test body", priority);

    [Fact]
    public async Task SendAsync_SucceedsFirstAttempt_ReturnsSent_RegardlessOfPriority()
    {
        var sender = new FakeEmailSender();
        var sut = new NotificationService(sender, new ListLogger<NotificationService>(), delay: NoOpDelay);

        var outcome = await sut.SendAsync(Request(NotificationPriority.Standard));

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Single(sender.SentEmails);
    }

    [Fact]
    public async Task SendAsync_Standard_FailsOnce_ReturnsFailedLoggedOnly_AndNeverRetries()
    {
        var sender = new FakeEmailSender { FailuresBeforeSuccess = int.MaxValue };
        var logger = new ListLogger<NotificationService>();
        var sut = new NotificationService(sender, logger, delay: NoOpDelay);

        var outcome = await sut.SendAsync(Request(NotificationPriority.Standard));

        Assert.Equal(NotificationOutcome.FailedLoggedOnly, outcome);
        Assert.Empty(sender.SentEmails);
        Assert.DoesNotContain(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task SendAsync_SafeguardingCritical_SucceedsAfterRetries_ReturnsSentAfterRetry()
    {
        // Default max attempts is 3 — fail the first 2, succeed on the 3rd.
        var sender = new FakeEmailSender { FailuresBeforeSuccess = 2 };
        var sut = new NotificationService(sender, new ListLogger<NotificationService>(), delay: NoOpDelay);

        var outcome = await sut.SendAsync(Request(NotificationPriority.SafeguardingCritical));

        Assert.Equal(NotificationOutcome.SentAfterRetry, outcome);
        Assert.Single(sender.SentEmails);
    }

    [Fact]
    public async Task SendAsync_SafeguardingCritical_AllAttemptsFail_ReturnsFailedAndEscalated_AndLogsError()
    {
        var sender = new FakeEmailSender { FailuresBeforeSuccess = int.MaxValue };
        var logger = new ListLogger<NotificationService>();
        var sut = new NotificationService(sender, logger, delay: NoOpDelay);

        var outcome = await sut.SendAsync(Request(NotificationPriority.SafeguardingCritical));

        Assert.Equal(NotificationOutcome.FailedAndEscalated, outcome);
        Assert.Empty(sender.SentEmails);
        var alert = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("SERVICE-ALERT:", alert.Message);
    }

    [Fact]
    public async Task SendAsync_SafeguardingCritical_AllAttemptsFail_ServiceAlertLineCarriesNoRecipientOrSubject()
    {
        // The SERVICE-ALERT line is what the operational digest emails to the alert address (monitoring.md
        // §3, Philip 29/10/2026); the recipient and subject stay in the journal's warning line only.
        var sender = new FakeEmailSender { FailuresBeforeSuccess = int.MaxValue };
        var logger = new ListLogger<NotificationService>();
        var sut = new NotificationService(sender, logger, delay: NoOpDelay);

        await sut.SendAsync(new NotificationRequest(
            "admin@example.com", "Admin Name", "VLMS safeguarding escalation: 1 issue(s) require attention",
            "- Consent: Ann Smith (student #4) has no valid consent on record.", NotificationPriority.SafeguardingCritical));

        var alert = Assert.Single(logger.Entries, e => e.Message.Contains("SERVICE-ALERT:"));
        Assert.DoesNotContain("admin@example.com", alert.Message);
        Assert.DoesNotContain("Admin Name", alert.Message);
        Assert.DoesNotContain("safeguarding escalation", alert.Message);
        Assert.DoesNotContain("Ann Smith", alert.Message);
        Assert.DoesNotContain("ESCALATION:", alert.Message);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("admin@example.com") && !e.Message.Contains("SERVICE-ALERT:"));
    }

    [Fact]
    public async Task SendAsync_SafeguardingCritical_RetriesLoggedAtWarning_BeforeFinalEscalationAtError()
    {
        var sender = new FakeEmailSender { FailuresBeforeSuccess = int.MaxValue };
        var logger = new ListLogger<NotificationService>();
        var sut = new NotificationService(sender, logger, safeguardingCriticalMaxAttempts: 3, delay: NoOpDelay);

        await sut.SendAsync(Request(NotificationPriority.SafeguardingCritical));

        var warningCount = logger.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        var errorCount = logger.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);

        Assert.Equal(3, warningCount); // attempts 1 and 2 retry-log at Warning; then the final failure's detail line
        Assert.Equal(1, errorCount); // attempt 3 (final): the SERVICE-ALERT line at Error
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveMaxAttempts(int invalidMaxAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationService(new FakeEmailSender(), new ListLogger<NotificationService>(), invalidMaxAttempts, NoOpDelay));
    }

    [Fact]
    public async Task SendAsync_MaxAttempts_IsConfigurable()
    {
        var sender = new FakeEmailSender { FailuresBeforeSuccess = 4 };
        var sut = new NotificationService(sender, new ListLogger<NotificationService>(), safeguardingCriticalMaxAttempts: 5, delay: NoOpDelay);

        var outcome = await sut.SendAsync(Request(NotificationPriority.SafeguardingCritical));

        Assert.Equal(NotificationOutcome.SentAfterRetry, outcome);
    }

    [Fact]
    public async Task SendAsync_SafeguardingCritical_DelaysBetweenRetryAttempts_ButNotAfterFinalAttempt()
    {
        var sender = new FakeEmailSender { FailuresBeforeSuccess = int.MaxValue };
        var delayCalls = new List<TimeSpan>();
        Task RecordingDelay(TimeSpan delay, CancellationToken ct)
        {
            delayCalls.Add(delay);
            return Task.CompletedTask;
        }

        var sut = new NotificationService(
            sender, new ListLogger<NotificationService>(), safeguardingCriticalMaxAttempts: 3, delay: RecordingDelay);

        await sut.SendAsync(Request(NotificationPriority.SafeguardingCritical));

        // 3 attempts, all fail: delay happens between attempt 1->2 and 2->3, but not after the
        // final (3rd) failed attempt — that's when escalation happens instead of another wait.
        Assert.Equal(2, delayCalls.Count);
        Assert.All(delayCalls, d => Assert.True(d > TimeSpan.Zero));
    }
}
