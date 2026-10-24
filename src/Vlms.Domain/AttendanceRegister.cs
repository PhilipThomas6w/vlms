namespace Vlms.Domain;

/// <summary>
/// One class's register at one <see cref="ChapterEvent"/> (functional.md FR-020, FR-022): one per
/// event per class (rank), with a present or absent mark for each student in the class. Opened from
/// its event; a class's registers for a day are collated into the day's total. Self-contained in its
/// chapter (docs/design/chapters-and-attendance.md section 5): only that chapter's staff take and read it.
/// </summary>
public sealed class AttendanceRegister : IChapterOwned
{
    /// <summary>The chapter this register belongs to (docs/adr/0010-chapters.md): always its event's. Left at 0, it is copied on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int ChapterEventId { get; init; }
    public required int RankId { get; init; }

    /// <summary>The event's date, copied so history and totals need not join the event.</summary>
    public required DateOnly MeetingDate { get; init; }
    public required int TakenByUserId { get; set; }
    public required DateTime TakenAt { get; set; }

    public ChapterEvent? ChapterEvent { get; init; }
    public Rank? Rank { get; init; }
    public AppUser? TakenByUser { get; init; }
    public ICollection<AttendanceMark> Marks { get; init; } = new List<AttendanceMark>();
}
