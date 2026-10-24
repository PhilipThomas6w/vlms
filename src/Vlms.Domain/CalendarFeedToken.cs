namespace Vlms.Domain;

/// <summary>
/// A person's private link for subscribing to their chapter's events in their own calendar app
/// (functional.md FR-022). Only a SHA-256 hash of the link's secret is kept, so the link is shown
/// once, when made; making a new one replaces the old. Like <see cref="ActiveChapterChoice"/> it is
/// the person's own setting, not a chapter record: the subscription endpoint finds it before anyone
/// is signed in, then serves only that chapter's events, and only while the person still belongs to it.
/// </summary>
public sealed class CalendarFeedToken
{
    public required int Id { get; init; }
    public required int UserId { get; init; }
    public required int ChapterId { get; init; }
    public required string TokenHash { get; init; }
    public required DateTime CreatedAt { get; init; }
}
