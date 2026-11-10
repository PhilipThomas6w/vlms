namespace Vlms.Domain;

public sealed class Rank
{
    public required int Id { get; init; }
    public required int Order { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    public ICollection<Lesson> Lessons { get; init; } = new List<Lesson>();
    public ICollection<RankBadge> RankBadges { get; init; } = new List<RankBadge>();

    public bool IsBefore(Rank other) => Order < other.Order;
}
