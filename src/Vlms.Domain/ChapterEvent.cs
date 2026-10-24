namespace Vlms.Domain;

/// <summary>
/// A day the chapter meets (functional.md FR-022): every class meets that day, and each class has its
/// own <see cref="AttendanceRegister"/> for it. Scheduled one at a time by the chapter's Admins, with
/// no repeating pattern (Philip, 24/10/2026); at most one per chapter per date.
/// </summary>
public sealed class ChapterEvent : IChapterOwned
{
    /// <summary>The chapter this event belongs to (docs/adr/0010-chapters.md). Left at 0, it is the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required DateOnly Date { get; set; }
    public required string Title { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? Location { get; set; }
    public required int CreatedByUserId { get; init; }
    public required DateTime CreatedAt { get; init; }

    public ICollection<AttendanceRegister> Registers { get; init; } = new List<AttendanceRegister>();
}
