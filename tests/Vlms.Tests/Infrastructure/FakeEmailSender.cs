using Vlms.Infrastructure.Notifications;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Configurable <see cref="IEmailSender"/> test double — same spirit as
/// <see cref="InMemoryBlobStorage"/>/<see cref="FakeCurrentUserContext"/>. Records every send
/// attempt and, when <see cref="FailuresBeforeSuccess"/> is greater than zero, throws for that many
/// leading attempts (per recipient) before succeeding — lets <c>NotificationServiceTests</c> exercise
/// "succeeds after N retries" and "never succeeds" without touching a real Azure Communication
/// Services resource.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly Dictionary<string, int> _attemptsByRecipient = new();

    public List<(string RecipientEmail, string Subject, string Body)> SentEmails { get; } = new();

    /// <summary>How many leading attempts (per recipient) throw before a send succeeds.
    /// <see cref="int.MaxValue"/> means "never succeeds".</summary>
    public int FailuresBeforeSuccess { get; set; }

    public Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
    {
        var attempt = _attemptsByRecipient.GetValueOrDefault(recipientEmail) + 1;
        _attemptsByRecipient[recipientEmail] = attempt;

        if (attempt <= FailuresBeforeSuccess)
        {
            throw new InvalidOperationException($"Simulated send failure (attempt {attempt}) to {recipientEmail}.");
        }

        SentEmails.Add((recipientEmail, subject, body));
        return Task.CompletedTask;
    }
}
