namespace Vlms.Domain;

/// <summary>
/// Promotion history: a new row starts when a student enters a rank, closes (<see cref="CompletedAt"/>)
/// when they complete it (auto-promotion trigger).
/// </summary>
public sealed class StudentRankProgress : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required int RankId { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    public Student? Student { get; init; }
    public Rank? Rank { get; init; }
}
