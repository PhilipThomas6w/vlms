namespace Vlms.Domain;

/// <summary>Awarded on promotion. Real tracked record, not implicit.</summary>
public sealed class StudentBadge : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required int RankBadgeId { get; init; }
    public required DateTime AwardedAt { get; init; }

    public Student? Student { get; init; }
    public RankBadge? RankBadge { get; init; }
}
