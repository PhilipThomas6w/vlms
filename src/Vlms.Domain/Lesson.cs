namespace Vlms.Domain;

/// <summary>Current live version of a lesson's content.</summary>
public sealed class Lesson
{
    public required int Id { get; init; }
    public required int RankId { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }

    /// <summary>Delivery order within the rank (1-based). Not unique: a retired lesson keeps its old position.</summary>
    public required int Order { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>
    /// The lesson text in Markdown (docs/adr/0012-app-hosted-lessons.md), or null for a lesson not yet converted,
    /// which keeps its <see cref="Files"/> only. Pictures are referred to by <see cref="LessonPicture.FileName"/>.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// Changes whenever <see cref="Body"/> or its pictures change (a hash of both; null when there is no body), so a
    /// copy saved on a device can tell it is out of date (FR-023, adr/0012 decision 7).
    /// </summary>
    public string? ContentVersion { get; init; }

    public Rank? Rank { get; init; }
    public ICollection<LessonTagAssignment> Tags { get; init; } = new List<LessonTagAssignment>();
    public ICollection<LessonFile> Files { get; init; } = new List<LessonFile>();
    public ICollection<LessonPicture> Pictures { get; init; } = new List<LessonPicture>();
    public ICollection<LessonChangeProposal> ChangeProposals { get; init; } = new List<LessonChangeProposal>();
    public ICollection<StudentLessonCompletion> Completions { get; init; } = new List<StudentLessonCompletion>();
}
