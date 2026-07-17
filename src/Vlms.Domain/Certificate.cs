namespace Vlms.Domain;

/// <summary>Auto-generated on completion (QuestPDF). Real tracked record, not implicit.</summary>
public sealed class Certificate
{
    public required int Id { get; init; }
    public required int StudentLessonCompletionId { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required string BlobKey { get; init; }

    public StudentLessonCompletion? StudentLessonCompletion { get; init; }
}
