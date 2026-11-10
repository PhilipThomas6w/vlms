namespace Vlms.Domain;

/// <summary>
/// One published version of a rank's Teacher's Information (<see cref="Rank.TeachersInformation"/>; docs/adr/0015
/// decision 4), kept permanently and numbered from 1 per rank, like a <see cref="LessonVersion"/>: the text and pictures
/// as they then were, and who suggested, approved or published it. <see cref="Kind"/> is
/// <see cref="LessonVersionKind.Converted"/> when the curriculum seeder wrote it (decision 5), otherwise
/// <see cref="LessonVersionKind.ApprovedSuggestion"/> or <see cref="LessonVersionKind.PublishedDirectly"/>.
/// </summary>
public sealed class TeachersInformationVersion
{
    public required int Id { get; init; }
    public required int RankId { get; init; }

    /// <summary>1, 2, 3... per rank.</summary>
    public required int Number { get; init; }

    public required LessonVersionKind Kind { get; init; }

    /// <summary>The Teacher's Information (Markdown); null when the rank had none.</summary>
    public string? Body { get; init; }

    /// <summary>Who suggested the change; null for a converted version, or once their account is deleted (docs/adr/0014).</summary>
    public int? SuggestedByUserId { get; init; }

    public DateTime? SuggestedAt { get; init; }

    /// <summary>The Approver who approved the suggestion; null when published directly or converted.</summary>
    public int? ApprovedByUserId { get; init; }

    /// <summary>The Approver who published it directly with "Publish now"; null otherwise.</summary>
    public int? PublishedByUserId { get; init; }

    /// <summary>True when it was published directly rather than approved (kept when the Approver's account is deleted).</summary>
    public bool PublishedDirectly { get; init; }

    public string? Reason { get; init; }

    /// <summary>When it became the rank's Teacher's Information.</summary>
    public required DateTime PublishedAt { get; init; }

    /// <summary>The suggestion it came from; null for a converted version.</summary>
    public int? SuggestionId { get; init; }

    public Rank? Rank { get; init; }
    public LessonSuggestion? Suggestion { get; init; }
    public ICollection<TeachersInformationVersionPicture> Pictures { get; init; } = new List<TeachersInformationVersionPicture>();
}

/// <summary>
/// A picture of a <see cref="TeachersInformationVersion"/>: the file name its text uses and the stored copy, whose blob key
/// is never overwritten with different content, so every version stays readable.
/// </summary>
public sealed class TeachersInformationVersionPicture
{
    public required int Id { get; init; }
    public required int TeachersInformationVersionId { get; init; }
    public required int Order { get; init; }
    public required string FileName { get; init; }
    public required string BlobKey { get; init; }

    public TeachersInformationVersion? TeachersInformationVersion { get; init; }
}
