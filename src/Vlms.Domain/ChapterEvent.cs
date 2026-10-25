namespace Vlms.Domain;

/// <summary>
/// What kind of event a <see cref="ChapterEvent"/> is (functional.md FR-022, Philip 25/10/2026): a
/// Varangians event (a class or an excursion), which has registers, or a parish event (a service or a
/// parish meeting), which has none. Only class absences count towards the at-risk rule (FR-020).
/// </summary>
public enum ChapterEventKind
{
    Class = 1,
    Excursion = 2,
    Service = 3,
    ParishMeeting = 4,
}

public static class ChapterEventKinds
{
    /// <summary>A Varangians event: a class or an excursion, with registers; at most one a day per chapter.</summary>
    public static bool IsVarangians(this ChapterEventKind kind) => kind is ChapterEventKind.Class or ChapterEventKind.Excursion;

    /// <summary>The kind as shown to people.</summary>
    public static string Label(this ChapterEventKind kind) => kind switch
    {
        ChapterEventKind.Class => "Class",
        ChapterEventKind.Excursion => "Excursion",
        ChapterEventKind.Service => "Service",
        ChapterEventKind.ParishMeeting => "Parish meeting",
        _ => kind.ToString(),
    };
}

/// <summary>
/// One of the chapter's events (functional.md FR-022). A class is a day every class meets, each with
/// its own <see cref="AttendanceRegister"/>; an excursion has one register for everyone; a service or
/// parish meeting has none. Scheduled by the chapter's Admins, one-off or as a repeating series
/// (<see cref="SeriesId"/>), each date then changed or removed on its own. At most one Varangians
/// event (class or excursion) per chapter per date; parish events are not limited.
/// </summary>
public sealed class ChapterEvent : IChapterOwned
{
    /// <summary>The chapter this event belongs to (docs/adr/0010-chapters.md). Left at 0, it is the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required DateOnly Date { get; set; }
    public required string Title { get; set; }
    public ChapterEventKind Kind { get; set; } = ChapterEventKind.Class;
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? Location { get; set; }

    /// <summary>The id of the series' first event, for every event created as part of a repeating series; null for a one-off event.</summary>
    public int? SeriesId { get; init; }
    public required int CreatedByUserId { get; init; }
    public required DateTime CreatedAt { get; init; }

    public ICollection<AttendanceRegister> Registers { get; init; } = new List<AttendanceRegister>();
}
