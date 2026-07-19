namespace Vlms.Domain;

/// <summary>
/// `docs/design/low-level-design.md`'s two-tier failure-handling rule (design-review addition):
/// "a failed send for a safeguarding-critical notification (expired consent, expired DBS) is
/// retried with backoff and, if still failing, logged as an escalation-visible failure to Admin...
/// Non-critical notifications (e.g. routine completion confirmations) log failure without
/// escalation." <see cref="INotificationService"/>'s real implementation branches its retry/
/// escalation behaviour on this value.
/// </summary>
public enum NotificationPriority
{
    Standard,
    SafeguardingCritical
}

/// <summary>One outbound notification. Content is caller-built (plain text) — no templating engine,
/// since nothing in the design docs asks for one at this scale.</summary>
public sealed record NotificationRequest(
    string RecipientEmail,
    string RecipientName,
    string Subject,
    string Body,
    NotificationPriority Priority);

/// <summary>What actually happened to a <see cref="NotificationRequest"/> — lets a caller (e.g.
/// tests, or a future digest summary) distinguish "sent first try" from "sent, but only after a
/// retry" from the two failure outcomes, rather than just a bool.</summary>
public enum NotificationOutcome
{
    Sent,
    SentAfterRetry,
    FailedLoggedOnly,
    FailedAndEscalated
}

/// <summary>
/// Wraps Azure Communication Services Email (`adr/0001-technology-stack.md`) for STATE.md's
/// "NotificationService (Azure Communication Services Email) with retry/escalation for
/// safeguarding-critical notifications" item. Lives in Vlms.Domain, alongside
/// <see cref="IBlobStorage"/>/<see cref="ICurrentUserContext"/>, for the same reason: a
/// technology-agnostic abstraction Vlms.Infrastructure implements against the real Azure SDK
/// (<c>Notifications/NotificationService.cs</c>) without Vlms.Domain taking on that dependency.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Never throws — a failed send (even after retry/escalation) is reported via the returned
    /// <see cref="NotificationOutcome"/>, not an exception, so a caller sending several
    /// notifications (e.g. one per Admin/SafeguardingOfficer recipient) can keep going after one
    /// recipient's send fails rather than aborting the whole batch.
    /// </summary>
    Task<NotificationOutcome> SendAsync(NotificationRequest request, CancellationToken ct = default);
}
