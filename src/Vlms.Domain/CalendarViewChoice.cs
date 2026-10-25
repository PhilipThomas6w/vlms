namespace Vlms.Domain;

/// <summary>How the calendar page shows a month (functional.md FR-022).</summary>
public enum CalendarView
{
    List = 1,
    Grid = 2,
}

/// <summary>
/// The calendar view a person last chose on the calendar page, kept for them on every device
/// (Philip, 25/10/2026). Until they choose, the page shows the list on phones and the grid on wider
/// screens. Like <see cref="ActiveChapterChoice"/>, the person's own setting in its own table, not a
/// chapter record.
/// </summary>
public sealed class CalendarViewChoice
{
    public required int UserId { get; init; }
    public required CalendarView View { get; set; }
}
