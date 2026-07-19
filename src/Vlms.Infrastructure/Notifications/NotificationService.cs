using Microsoft.Extensions.Logging;
using Vlms.Domain;

namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// Real <see cref="INotificationService"/> implementation — the retry/escalation logic
/// docs/design/low-level-design.md's "NotificationService" paragraph describes ("Failure handling
/// (design-review addition): a failed send for a safeguarding-critical notification (expired
/// consent, expired DBS) is retried with backoff and, if still failing, logged as an
/// escalation-visible failure to Admin — a silent failure here must not be possible... Non-critical
/// notifications... log failure without escalation.").
///
/// <see cref="NotificationPriority.SafeguardingCritical"/> requests get up to
/// <see cref="_safeguardingCriticalMaxAttempts"/> attempts with a backoff delay between them
/// (<see cref="RetryDelays"/>, a documented build-time default — the design doc says "with backoff"
/// but names no figure, same "configurable, no number given" situation
/// <c>ConsentExpiryJob.DefaultExpiryWarningWindowDays</c> resolved); if every attempt fails, the
/// final failure is logged at <see cref="LogLevel.Error"/> (an "escalation-visible failure",
/// matching the same ILogger-feeds-Application-Insights reasoning adr/0003/ConsentExpiryJob already
/// use) and <see cref="NotificationOutcome.FailedAndEscalated"/> is returned — never an exception,
/// so a caller sending to several recipients can continue past one failure.
///
/// <see cref="NotificationPriority.Standard"/> requests get exactly one attempt; a failure is
/// logged at <see cref="LogLevel.Warning"/> (visible, but not escalation-visible) and
/// <see cref="NotificationOutcome.FailedLoggedOnly"/> is returned.
///
/// Depends on <see cref="IEmailSender"/> (not the Azure SDK directly) so tests can inject a fake
/// that fails on demand — see <c>NotificationServiceTests</c>. The <paramref name="delay"/>
/// constructor parameter exists purely so tests don't have to wait through a real backoff, the same
/// reason <c>ConsentExpiryJob</c> takes <c>expiryWarningWindowDays</c> as a constructor parameter
/// rather than a hardcoded value.
/// </summary>
public sealed class NotificationService : INotificationService
{
    public const int DefaultSafeguardingCriticalMaxAttempts = 3;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    private readonly IEmailSender _emailSender;
    private readonly ILogger<NotificationService> _logger;
    private readonly int _safeguardingCriticalMaxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public NotificationService(
        IEmailSender emailSender,
        ILogger<NotificationService> logger,
        int safeguardingCriticalMaxAttempts = DefaultSafeguardingCriticalMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (safeguardingCriticalMaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(safeguardingCriticalMaxAttempts), safeguardingCriticalMaxAttempts, "Must be at least 1.");
        }

        _emailSender = emailSender;
        _logger = logger;
        _safeguardingCriticalMaxAttempts = safeguardingCriticalMaxAttempts;
        _delay = delay ?? Task.Delay;
    }

    public async Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        var maxAttempts = request.Priority == NotificationPriority.SafeguardingCritical
            ? _safeguardingCriticalMaxAttempts
            : 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _emailSender.SendAsync(request.RecipientEmail, request.RecipientName, request.Subject, request.Body, ct);
                return attempt == 1 ? NotificationOutcome.Sent : NotificationOutcome.SentAfterRetry;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Notification send attempt {Attempt}/{MaxAttempts} to {RecipientEmail} failed ({Priority}) — retrying.",
                    attempt, maxAttempts, request.RecipientEmail, request.Priority);

                var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                await _delay(delay, ct);
            }
            catch (Exception ex) when (request.Priority == NotificationPriority.SafeguardingCritical)
            {
                _logger.LogError(
                    ex,
                    "ESCALATION: safeguarding-critical notification to {RecipientEmail} failed after {MaxAttempts} attempt(s) — subject: {Subject}.",
                    request.RecipientEmail, maxAttempts, request.Subject);
                return NotificationOutcome.FailedAndEscalated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Notification to {RecipientEmail} failed — subject: {Subject}.",
                    request.RecipientEmail, request.Subject);
                return NotificationOutcome.FailedLoggedOnly;
            }
        }

        // Unreachable: the loop's last iteration (attempt == maxAttempts) always falls into one of
        // the two non-retrying catch clauses above on failure, or returns on success.
        throw new InvalidOperationException("NotificationService.SendAsync: unreachable retry-loop exit.");
    }
}
