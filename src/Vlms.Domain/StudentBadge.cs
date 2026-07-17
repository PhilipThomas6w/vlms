namespace Vlms.Domain;

/// <summary>Awarded on promotion. Real tracked record, not implicit.</summary>
public sealed class StudentBadge
{
    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required int RankBadgeId { get; init; }
    public required DateTime AwardedAt { get; init; }

    public Student? Student { get; init; }
    public RankBadge? RankBadge { get; init; }
}
