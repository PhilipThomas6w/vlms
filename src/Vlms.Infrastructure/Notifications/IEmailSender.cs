namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// The raw "send one email, throw on failure" seam <see cref="NotificationService"/>'s retry/
/// escalation loop tests against — kept separate from <see cref="Vlms.Domain.INotificationService"/>
/// itself so tests can inject a fake that fails on demand (same spirit as
/// <c>InMemoryBlobStorage</c>/<c>ListLogger</c>) without needing to mock Azure Communication
/// Services' sealed <c>EmailClient</c>. Infrastructure-only — nothing in Vlms.Domain or Vlms.Web
/// needs to see this, only <see cref="NotificationService"/>'s own retry logic does.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends one email. Throws if the send did not succeed.</summary>
    Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default);
}
