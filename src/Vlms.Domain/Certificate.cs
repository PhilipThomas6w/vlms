namespace Vlms.Domain;

/// <summary>Auto-generated on completion (QuestPDF). Real tracked record, not implicit.</summary>
public sealed class Certificate : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int StudentLessonCompletionId { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required string BlobKey { get; init; }

    public StudentLessonCompletion? StudentLessonCompletion { get; init; }
}
