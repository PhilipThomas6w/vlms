namespace Vlms.Domain;

/// <summary>One row per (LessonId, Tag) pair — same shape as <see cref="UserRole"/>.</summary>
public sealed class LessonTagAssignment
{
    public required int LessonId { get; init; }
    public required LessonTag Tag { get; init; }

    public Lesson? Lesson { get; init; }
}
