namespace Vlms.Domain;

public sealed class Rank
{
    public required int Id { get; init; }
    public required int Order { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// The rank's Teacher's Information in Markdown (docs/adr/0012-app-hosted-lessons.md decision 5), shown at the
    /// top of the rank's lessons to Teachers, Admins and the National Administrator only; null when there is none.
    /// </summary>
    public string? TeachersInformation { get; init; }

    public ICollection<Lesson> Lessons { get; init; } = new List<Lesson>();
    public ICollection<RankBadge> RankBadges { get; init; } = new List<RankBadge>();
    public ICollection<TeachersInformationPicture> TeachersInformationPictures { get; init; } = new List<TeachersInformationPicture>();

    public bool IsBefore(Rank other) => Order < other.Order;
}
