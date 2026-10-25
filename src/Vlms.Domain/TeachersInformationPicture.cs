namespace Vlms.Domain;

/// <summary>
/// A picture in a rank's Teacher's Information (<see cref="Rank.TeachersInformation"/>;
/// docs/adr/0012-app-hosted-lessons.md decision 5). Same shape as <see cref="LessonPicture"/>, but staff-only:
/// served by id only at <c>GET /lessons/teachers-information/pictures/{id}</c> to Teachers, Admins and the
/// National Administrator, never through the lesson-picture endpoint students and parents can use.
/// </summary>
public sealed class TeachersInformationPicture
{
    public required int Id { get; init; }
    public required int RankId { get; init; }

    /// <summary>Order of first appearance in the text (1-based).</summary>
    public required int Order { get; init; }

    public required string FileName { get; init; }

    public required string BlobKey { get; init; }

    public Rank? Rank { get; init; }
}
