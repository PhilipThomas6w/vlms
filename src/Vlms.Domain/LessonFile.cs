namespace Vlms.Domain;

/// <summary>
/// One handout of a lesson as it is now (docs/adr/0016-lesson-handouts.md): a printable that goes with the lesson (a
/// template, worksheet, colouring page), downloadable by everyone who can see the lesson. A lesson can have several.
/// Handouts are changed only through a lesson suggestion (adr/0016 decision 2) or by the curriculum seeder; each version of
/// the lesson keeps its own list (<see cref="LessonVersionHandout"/>). Ids stay stable while a handout remains, because
/// copies saved on devices refer to them (FR-023).
/// </summary>
public sealed class LessonFile
{
    public required int Id { get; init; }
    public required int LessonId { get; init; }

    /// <summary>Display order within the lesson (1-based).</summary>
    public required int Order { get; init; }

    /// <summary>The stored file name, used when it is downloaded, e.g. "Fasting dial template.pdf". Never a path.</summary>
    public required string FileName { get; init; }

    /// <summary>The name shown to people, e.g. "Fasting dial template" (adr/0016; editable in a suggestion).</summary>
    public required string DisplayName { get; init; }

    public required string BlobKey { get; init; }

    public Lesson? Lesson { get; init; }
}
