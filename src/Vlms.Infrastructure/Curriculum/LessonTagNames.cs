using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>Human-readable names for <see cref="LessonTag"/> values, for display in the lesson library.</summary>
public static class LessonTagNames
{
    public static string Display(LessonTag tag) => tag switch
    {
        LessonTag.ChurchLife => "Church life",
        LessonTag.Worship => "Worship",
        LessonTag.ChurchInterior => "Church interior",
        LessonTag.Mysteries => "Mysteries",
        LessonTag.Prayers => "Prayers",
        LessonTag.Saints => "Saints",
        LessonTag.ChurchHistory => "Church history",
        LessonTag.OldTestament => "Old Testament",
        LessonTag.NewTestament => "New Testament",
        LessonTag.ChurchYear => "Church year",
        LessonTag.ChristianLife => "Christian life",
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "No display name for this lesson tag.")
    };
}
