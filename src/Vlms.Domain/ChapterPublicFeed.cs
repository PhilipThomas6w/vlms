namespace Vlms.Domain;

/// <summary>
/// A chapter's public link for subscribing to its parish events (services, parish meetings) in a
/// calendar app (functional.md FR-022; Philip, 25/10/2026): one per chapter, made, replaced or stopped
/// by the chapter's Admins, and meant to be shared, for example on the parish website. Varangians
/// events (classes, excursions) are never in it; they stay on each person's private link
/// (<see cref="CalendarFeedToken"/>), for safeguarding. The key is not a credential to anything
/// private, so it is kept as it is and shown to everyone in the chapter. Found before anyone is
/// signed in, so, like <see cref="CalendarFeedToken"/>, it is not a chapter-filtered record.
/// </summary>
public sealed class ChapterPublicFeed
{
    public required int ChapterId { get; init; }

    /// <summary>The random key in the link's address.</summary>
    public required string Key { get; init; }

    public required DateTime CreatedAt { get; init; }

    /// <summary>The Admin who made it; null once that person's record has been removed.</summary>
    public int? CreatedByUserId { get; init; }
}
