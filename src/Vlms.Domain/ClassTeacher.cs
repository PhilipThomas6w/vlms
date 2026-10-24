namespace Vlms.Domain;

/// <summary>
/// A Teacher assigned by an Admin to teach a class (functional.md FR-022). Classes are divided by
/// rank, one class per <see cref="Rank"/>, so a student changes class on promotion. A Teacher can
/// teach several classes and a class can have several Teachers. A class's teachers normally take its
/// register, but any Teacher in the chapter may take or update any class's register.
/// </summary>
public sealed class ClassTeacher : IChapterOwned
{
    /// <summary>The chapter this assignment belongs to (docs/adr/0010-chapters.md). Left at 0, it is the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int RankId { get; init; }
    public required int TeacherUserId { get; init; }

    public Rank? Rank { get; init; }
    public AppUser? TeacherUser { get; init; }
}
