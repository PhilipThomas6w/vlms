using Vlms.Domain;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Records every <see cref="NotificationRequest"/> passed to <see cref="SendAsync"/> — used by
/// <c>ConsentExpiryNotifierTests</c> to verify which recipients/content a caller built, without
/// needing a real <see cref="INotificationService"/> implementation (that's
/// <c>NotificationServiceTests</c>' job, using <see cref="FakeEmailSender"/> instead). Same "no
/// mocking library" convention as <see cref="FakeCurrentUserContext"/>/<see cref="ListLogger{T}"/>.
/// </summary>
public sealed class FakeNotificationService : INotificationService
{
    public List<NotificationRequest> SentRequests { get; } = new();

    public NotificationOutcome OutcomeToReturn { get; set; } = NotificationOutcome.Sent;

    public Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        SentRequests.Add(request);
        return Task.FromResult(OutcomeToReturn);
    }
}
