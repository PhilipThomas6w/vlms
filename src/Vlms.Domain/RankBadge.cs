namespace Vlms.Domain;

/// <summary>One badge per rank.</summary>
public sealed class RankBadge
{
    public required int Id { get; init; }
    public required int RankId { get; init; }
    public required string ImageBlobKey { get; init; }

    public Rank? Rank { get; init; }
    public ICollection<StudentBadge> StudentBadges { get; init; } = new List<StudentBadge>();
}
