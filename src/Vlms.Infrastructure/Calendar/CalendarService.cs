using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Calendar;

/// <summary>A chapter event as the calendar shows it.</summary>
public sealed record CalendarEventItem(int Id, DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location, ChapterEventKind Kind = ChapterEventKind.Class);

/// <summary>One day of a month: its main feast and fast (when imported) and the chapter's events.</summary>
public sealed record CalendarDay(DateOnly Date, string? Feast, LiturgicalFast Fast, IReadOnlyList<CalendarEventItem> Events);

/// <summary>A month of the calendar, every day in order.</summary>
public sealed record CalendarMonth(int Year, int Month, IReadOnlyList<CalendarDay> Days);

/// <summary>A day's saints and readings, opened from the calendar.</summary>
public sealed record LiturgicalDayDetail(DateOnly Date, string Title, LiturgicalFast Fast, IReadOnlyList<string> Saints, IReadOnlyList<ParsedReading> Readings);

/// <summary>
/// An event's details, whether any register has been taken at it, and, for an event in a repeating
/// series, how many of the series' dates come after it.
/// </summary>
public sealed record EventDetail(
    int Id, DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location, bool HasRegisters,
    ChapterEventKind Kind = ChapterEventKind.Class, int? SeriesId = null, int LaterInSeries = 0);

/// <summary>What an Admin enters for an event.</summary>
public sealed record EventInput(
    DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location, ChapterEventKind Kind = ChapterEventKind.Class);

/// <summary>How a series repeats (functional.md FR-022, Philip 25/10/2026).</summary>
public enum RepeatPattern
{
    /// <summary>Every week, on the first date's weekday.</summary>
    Weekly = 1,

    /// <summary>Every month, on the first date's weekday in the same week of the month (for example the third Sunday).</summary>
    MonthlyByWeekday = 2,

    /// <summary>Every year, on the first date's day and month.</summary>
    Yearly = 3,
}

/// <summary>A repeating series: its pattern, from the event's date up to and including <see cref="Until"/>.</summary>
public sealed record EventRepeat(RepeatPattern Pattern, DateOnly Until);

/// <summary>What removing an event did: how many dates were removed, and how many later dates were kept because a register had been taken at them.</summary>
public sealed record EventRemoval(int Removed, int KeptWithRegisters);

/// <summary>
/// The calendar (functional.md FR-022): the Church's liturgical calendar (<see cref="LiturgicalDay"/>,
/// national) alongside the chapter's own events (<see cref="ChapterEvent"/>, the days the chapter
/// meets, and its parish events). Everyone working in the chapter reads it: staff, parents and
/// students. Only the chapter's Admins schedule, change and remove events: one-off, or a repeating
/// series (weekly, monthly on the nth weekday, or yearly, with an end date) whose dates are then
/// changed or removed one at a time, or removed with every later date of the series (Philip,
/// 25/10/2026). At most one Varangians event (class or excursion) a day; parish events (services,
/// parish meetings) are not limited. The chapter query filter keeps every event read and write to
/// the caller's active chapter.
/// </summary>
public sealed class CalendarService
{
    public const int TitleMaxLength = 200;
    public const int LocationMaxLength = 200;

    /// <summary>The longest a series may run: its end date is at most this many years after its first date.</summary>
    public const int MaxRepeatYears = 5;

    private static readonly Role[] ChapterRoles = [Role.Admin, Role.Teacher, Role.SafeguardingOfficer, Role.Parent, Role.Student];

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public CalendarService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Whether the caller may read the calendar: they hold a role in the chapter they are working in.</summary>
    public bool CanRead => _currentUser.ActiveChapterId is not null && ChapterRoles.Any(_currentUser.HasRole);

    /// <summary>Every day of <paramref name="month"/> in <paramref name="year"/>, with its feast, fast and the chapter's events.</summary>
    public async Task<CalendarMonth> GetMonthAsync(int year, int month, CancellationToken ct = default)
    {
        RequireReader();

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var feasts = await _db.LiturgicalDays.AsNoTracking()
            .Where(d => d.Date >= first && d.Date <= last)
            .Select(d => new { d.Date, d.Title, d.Fast })
            .ToDictionaryAsync(d => d.Date, ct);
        var events = (await _db.ChapterEvents.AsNoTracking()
                .Where(e => e.Date >= first && e.Date <= last)
                .Select(e => new CalendarEventItem(e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location, e.Kind))
                .ToListAsync(ct))
            .ToLookup(e => e.Date);

        var days = new List<CalendarDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var feast = feasts.GetValueOrDefault(date);
            days.Add(new CalendarDay(date, feast?.Title, feast?.Fast ?? LiturgicalFast.None, events[date].OrderBy(e => e.StartTime).ToList()));
        }

        return new CalendarMonth(year, month, days);
    }

    /// <summary>The view the caller last chose for the calendar page, or null if they have not chosen.</summary>
    public async Task<CalendarView?> GetViewAsync(CancellationToken ct = default)
    {
        var userId = RequireReaderId();
        return await _db.CalendarViewChoices.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (CalendarView?)c.View)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Remembers <paramref name="view"/> as the caller's calendar view, on every device.</summary>
    public async Task SetViewAsync(CalendarView view, CancellationToken ct = default)
    {
        var userId = RequireReaderId();
        if (!Enum.IsDefined(view))
        {
            throw new ArgumentOutOfRangeException(nameof(view));
        }

        var choice = await _db.CalendarViewChoices.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (choice is null)
        {
            _db.CalendarViewChoices.Add(new CalendarViewChoice { UserId = userId, View = view });
        }
        else
        {
            choice.View = view;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The chapter's events from <paramref name="from"/> onwards, soonest first.</summary>
    public async Task<IReadOnlyList<CalendarEventItem>> GetUpcomingAsync(DateOnly from, int take = 5, CancellationToken ct = default)
    {
        RequireReader();

        return await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Date >= from)
            .OrderBy(e => e.Date)
            .Take(take)
            .Select(e => new CalendarEventItem(e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location, e.Kind))
            .ToListAsync(ct);
    }

    /// <summary>The saints and readings of <paramref name="date"/>, or null if the feed has nothing for it.</summary>
    public async Task<LiturgicalDayDetail?> GetLiturgicalDayAsync(DateOnly date, CancellationToken ct = default)
    {
        RequireReader();

        var day = await _db.LiturgicalDays.AsNoTracking()
            .Include(d => d.Readings)
            .SingleOrDefaultAsync(d => d.Date == date, ct);
        if (day is null)
        {
            return null;
        }

        var saints = day.Saints.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var readings = day.Readings.OrderBy(r => r.Order).Select(r => new ParsedReading(r.Heading, r.Reference, r.Text)).ToList();
        return new LiturgicalDayDetail(day.Date, day.Title, day.Fast, saints, readings);
    }

    /// <summary>One of the chapter's events, or null if it is not this chapter's.</summary>
    public async Task<EventDetail?> GetEventAsync(int eventId, CancellationToken ct = default)
    {
        RequireReader();

        return await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new EventDetail(
                e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location, e.Registers.Any(), e.Kind, e.SeriesId,
                e.SeriesId == null ? 0 : _db.ChapterEvents.Count(o => o.SeriesId == e.SeriesId && o.Date > e.Date)))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Schedules an event in the caller's chapter (Admin only), or, with <paramref name="repeat"/>, a
    /// series of them, one per date of the pattern up to its end date; returns the (first) event's id.
    /// A Varangians event is refused on a date that already has one, and a series is refused whole if
    /// any of its dates does.
    /// </summary>
    public async Task<int> CreateEventAsync(EventInput input, EventRepeat? repeat = null, CancellationToken ct = default)
    {
        var userId = RequireAdmin();
        var clean = Validate(input);
        var dates = repeat is null ? [clean.Date] : RepeatDates(clean.Date, repeat);
        if (clean.Kind.IsVarangians())
        {
            var taken = await _db.ChapterEvents.AsNoTracking()
                .Where(e => dates.Contains(e.Date) && (e.Kind == ChapterEventKind.Class || e.Kind == ChapterEventKind.Excursion))
                .Select(e => e.Date)
                .OrderBy(d => d)
                .ToListAsync(ct);
            if (taken.Count > 0)
            {
                var named = string.Join(", ", taken.Take(3).Select(d => d.ToString("d MMMM yyyy")));
                throw new InvalidOperationException(taken.Count == 1
                    ? $"There is already a class or excursion on {named}. The chapter has at most one a day."
                    : $"There is already a class or excursion on {taken.Count} of these dates ({named}{(taken.Count > 3 ? ", …" : "")}). The chapter has at most one a day.");
            }
        }

        var firstId = await _db.NextIdAsync<ChapterEvent>(ct);
        var id = firstId;
        var now = DateTime.UtcNow;
        foreach (var date in dates)
        {
            _db.ChapterEvents.Add(new ChapterEvent
            {
                Id = id++,
                Date = date,
                Title = clean.Title,
                Kind = clean.Kind,
                StartTime = clean.StartTime,
                EndTime = clean.EndTime,
                Location = clean.Location,
                SeriesId = repeat is null ? null : firstId,
                CreatedByUserId = userId,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        return firstId;
    }

    /// <summary>
    /// The dates of a series starting on <paramref name="first"/>, in order, first included. Refuses an
    /// end date before the first date or more than <see cref="MaxRepeatYears"/> years after it. A
    /// monthly series skips a month without that weekday in that week (a fifth Sunday); a yearly
    /// series from 29 February falls only in leap years.
    /// </summary>
    public static IReadOnlyList<DateOnly> RepeatDates(DateOnly first, EventRepeat repeat)
    {
        if (!Enum.IsDefined(repeat.Pattern))
        {
            throw new InvalidOperationException("Choose how the event repeats.");
        }

        if (repeat.Until < first)
        {
            throw new InvalidOperationException("The last date of a repeating event must be on or after its first date.");
        }

        if (repeat.Until > first.AddYears(MaxRepeatYears))
        {
            throw new InvalidOperationException($"A repeating event can run for at most {MaxRepeatYears} years.");
        }

        var dates = new List<DateOnly>();
        switch (repeat.Pattern)
        {
            case RepeatPattern.Weekly:
                for (var date = first; date <= repeat.Until; date = date.AddDays(7))
                {
                    dates.Add(date);
                }

                break;

            case RepeatPattern.MonthlyByWeekday:
                var week = (first.Day - 1) / 7;
                for (var month = new DateOnly(first.Year, first.Month, 1); month <= repeat.Until; month = month.AddMonths(1))
                {
                    var firstOfWeekday = month.AddDays(((int)first.DayOfWeek - (int)month.DayOfWeek + 7) % 7);
                    var date = firstOfWeekday.AddDays(7 * week);
                    if (date.Month == month.Month && date >= first && date <= repeat.Until)
                    {
                        dates.Add(date);
                    }
                }

                break;

            case RepeatPattern.Yearly:
                for (var year = first.Year; year <= repeat.Until.Year; year++)
                {
                    if (first.Month == 2 && first.Day == 29 && !DateTime.IsLeapYear(year))
                    {
                        continue;
                    }

                    var date = new DateOnly(year, first.Month, first.Day);
                    if (date <= repeat.Until)
                    {
                        dates.Add(date);
                    }
                }

                break;
        }

        return dates;
    }

    /// <summary>
    /// Changes one event (Admin only), on its own even when it is part of a series. Its date and its
    /// kind cannot change once a register has been taken at it.
    /// </summary>
    public async Task UpdateEventAsync(int eventId, EventInput input, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = Validate(input);
        var chapterEvent = await _db.ChapterEvents.SingleOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new InvalidOperationException("That event is not in this chapter's calendar.");

        if (clean.Date != chapterEvent.Date || clean.Kind != chapterEvent.Kind)
        {
            if (await _db.AttendanceRegisters.AnyAsync(r => r.ChapterEventId == eventId, ct))
            {
                throw new InvalidOperationException(clean.Date != chapterEvent.Date
                    ? "A register has been taken at this event, so its date can't change."
                    : "A register has been taken at this event, so its kind can't change.");
            }

            if (clean.Kind.IsVarangians())
            {
                await RequireDateFreeAsync(clean.Date, eventId, ct);
            }
        }

        chapterEvent.Date = clean.Date;
        chapterEvent.Kind = clean.Kind;
        chapterEvent.Title = clean.Title;
        chapterEvent.StartTime = clean.StartTime;
        chapterEvent.EndTime = clean.EndTime;
        chapterEvent.Location = clean.Location;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes an event (Admin only), unless a register has been taken at it. With
    /// <paramref name="andLaterInSeries"/>, also removes every later date of its series, keeping any
    /// at which a register has been taken.
    /// </summary>
    public async Task<EventRemoval> DeleteEventAsync(int eventId, bool andLaterInSeries = false, CancellationToken ct = default)
    {
        RequireAdmin();
        var chapterEvent = await _db.ChapterEvents.SingleOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new InvalidOperationException("That event is not in this chapter's calendar.");
        if (await _db.AttendanceRegisters.AnyAsync(r => r.ChapterEventId == eventId, ct))
        {
            throw new InvalidOperationException("A register has been taken at this event, so it can't be removed.");
        }

        var removed = new List<ChapterEvent> { chapterEvent };
        var kept = 0;
        if (andLaterInSeries && chapterEvent.SeriesId is { } seriesId)
        {
            var later = await _db.ChapterEvents
                .Where(e => e.SeriesId == seriesId && e.Date > chapterEvent.Date)
                .Select(e => new { Event = e, HasRegisters = e.Registers.Any() })
                .ToListAsync(ct);
            removed.AddRange(later.Where(l => !l.HasRegisters).Select(l => l.Event));
            kept = later.Count(l => l.HasRegisters);
        }

        _db.ChapterEvents.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);
        return new EventRemoval(removed.Count, kept);
    }

    private static EventInput Validate(EventInput input)
    {
        var title = input.Title?.Trim() ?? "";
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Enter a name for the event.");
        }

        if (title.Length > TitleMaxLength)
        {
            throw new InvalidOperationException($"The event's name must be {TitleMaxLength} characters or fewer.");
        }

        var location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim();
        if (location?.Length > LocationMaxLength)
        {
            throw new InvalidOperationException($"The place must be {LocationMaxLength} characters or fewer.");
        }

        if (input.EndTime is not null && input.StartTime is null)
        {
            throw new InvalidOperationException("Enter a start time as well as an end time.");
        }

        if (input.StartTime is { } start && input.EndTime is { } end && end <= start)
        {
            throw new InvalidOperationException("The end time must be after the start time.");
        }

        if (!Enum.IsDefined(input.Kind))
        {
            throw new InvalidOperationException("Choose what kind of event this is.");
        }

        return input with { Title = title, Location = location };
    }

    private async Task RequireDateFreeAsync(DateOnly date, int? exceptEventId, CancellationToken ct)
    {
        if (await _db.ChapterEvents.AnyAsync(e => e.Date == date && e.Id != exceptEventId
                && (e.Kind == ChapterEventKind.Class || e.Kind == ChapterEventKind.Excursion), ct))
        {
            throw new InvalidOperationException($"There is already a class or excursion on {date:d MMMM yyyy}. The chapter has at most one a day.");
        }
    }

    private void RequireReader()
    {
        if (!CanRead)
        {
            throw new UnauthorizedAccessException("Caller must hold a role in the chapter they are working in to see its calendar.");
        }
    }

    private int RequireReaderId()
    {
        RequireReader();
        return _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
    }

    private int RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || _currentUser.ActiveChapterId is null)
        {
            throw new UnauthorizedAccessException("Caller must be an Admin of the chapter to schedule its events.");
        }

        return _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
    }
}
