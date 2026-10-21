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

    public Rank? Rank { get; init; }
    public ICollection<LessonTagAssignment> Tags { get; init; } = new List<LessonTagAssignment>();
    public ICollection<LessonFile> Files { get; init; } = new List<LessonFile>();
    public ICollection<LessonChangeProposal> ChangeProposals { get; init; } = new List<LessonChangeProposal>();
    public ICollection<StudentLessonCompletion> Completions { get; init; } = new List<StudentLessonCompletion>();
}
