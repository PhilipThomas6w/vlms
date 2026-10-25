namespace Vlms.Domain;

/// <summary>
/// A picture shown in a converted lesson's text (<see cref="Lesson.Body"/>; docs/adr/0012-app-hosted-lessons.md
/// decision 4): the resized copy (about 1,600 px on the long side) of a picture the lesson text refers to by
/// <see cref="FileName"/>. Kept apart from <see cref="LessonFile"/>, the downloadable files of the lesson library,
/// so proposals and the library's file list never touch it. Served by id only (<c>GET /lessons/pictures/{id}</c>).
/// </summary>
public sealed class LessonPicture
{
    public required int Id { get; init; }
    public required int LessonId { get; init; }

    /// <summary>Order of first appearance in the lesson text (1-based).</summary>
    public required int Order { get; init; }

    /// <summary>The name the lesson text uses for it, e.g. "all-saints.jpg".</summary>
    public required string FileName { get; init; }

    public required string BlobKey { get; init; }

    public Lesson? Lesson { get; init; }
}
