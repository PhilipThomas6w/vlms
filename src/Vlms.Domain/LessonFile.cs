namespace Vlms.Domain;

/// <summary>
/// One content file of a lesson (the main document, an image, a handout). A lesson can have
/// several (docs/requirements/functional.md "Curriculum management"; Philip's decision, 21/10/2026).
/// </summary>
public sealed class LessonFile
{
    public required int Id { get; init; }
    public required int LessonId { get; init; }

    /// <summary>Display order within the lesson (1-based).</summary>
    public required int Order { get; init; }

    /// <summary>The file's display name, e.g. "Pentecost.docx".</summary>
    public required string FileName { get; init; }

    public required string BlobKey { get; init; }

    public Lesson? Lesson { get; init; }
}
