using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Calendar;

/// <summary>A chapter event as the calendar shows it.</summary>
public sealed record CalendarEventItem(int Id, DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location);

/// <summary>One day of a month: its main feast and fast (when imported) and the chapter's events.</summary>
public sealed record CalendarDay(DateOnly Date, string? Feast, LiturgicalFast Fast, IReadOnlyList<CalendarEventItem> Events);

/// <summary>A month of the calendar, every day in order.</summary>
public sealed record CalendarMonth(int Year, int Month, IReadOnlyList<CalendarDay> Days);

/// <summary>A day's saints and readings, opened from the calendar.</summary>
public sealed record LiturgicalDayDetail(DateOnly Date, string Title, LiturgicalFast Fast, IReadOnlyList<string> Saints, IReadOnlyList<ParsedReading> Readings);

/// <summary>An event's details, and whether any register has been taken at it.</summary>
public sealed record EventDetail(int Id, DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location, bool HasRegisters);

/// <summary>What an Admin enters for an event.</summary>
public sealed record EventInput(DateOnly Date, string Title, TimeOnly? StartTime, TimeOnly? EndTime, string? Location);

/// <summary>
/// The calendar (functional.md FR-022): the Church's liturgical calendar (<see cref="LiturgicalDay"/>,
/// national) alongside the chapter's own events (<see cref="ChapterEvent"/>, the days the chapter
/// meets). Everyone working in the chapter reads it: staff, parents and students. Only the chapter's
/// Admins schedule, change and remove events, one at a time; there is no repeating pattern
/// (Philip, 24/10/2026). The chapter query filter keeps every event read and write to the caller's
/// active chapter.
/// </summary>
public sealed class CalendarService
{
    public const int TitleMaxLength = 200;
    public const int LocationMaxLength = 200;

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
                .Select(e => new CalendarEventItem(e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location))
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
            .Select(e => new CalendarEventItem(e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location))
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
            .Select(e => new EventDetail(e.Id, e.Date, e.Title, e.StartTime, e.EndTime, e.Location, e.Registers.Any()))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Schedules an event in the caller's chapter (Admin only); returns its id.</summary>
    public async Task<int> CreateEventAsync(EventInput input, CancellationToken ct = default)
    {
        var userId = RequireAdmin();
        var clean = Validate(input);
        await RequireDateFreeAsync(clean.Date, exceptEventId: null, ct);

        var chapterEvent = new ChapterEvent
        {
            Id = await _db.NextIdAsync<ChapterEvent>(ct),
            Date = clean.Date,
            Title = clean.Title,
            StartTime = clean.StartTime,
            EndTime = clean.EndTime,
            Location = clean.Location,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ChapterEvents.Add(chapterEvent);
        await _db.SaveChangesAsync(ct);
        return chapterEvent.Id;
    }

    /// <summary>Changes an event (Admin only). Its date cannot change once a register has been taken at it.</summary>
    public async Task UpdateEventAsync(int eventId, EventInput input, CancellationToken ct = default)
    {
        RequireAdmin();
        var clean = Validate(input);
        var chapterEvent = await _db.ChapterEvents.SingleOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new InvalidOperationException("That event is not in this chapter's calendar.");

        if (clean.Date != chapterEvent.Date)
        {
            if (await _db.AttendanceRegisters.AnyAsync(r => r.ChapterEventId == eventId, ct))
            {
                throw new InvalidOperationException("A register has been taken at this event, so its date can't change.");
            }

            await RequireDateFreeAsync(clean.Date, eventId, ct);
        }

        chapterEvent.Date = clean.Date;
        chapterEvent.Title = clean.Title;
        chapterEvent.StartTime = clean.StartTime;
        chapterEvent.EndTime = clean.EndTime;
        chapterEvent.Location = clean.Location;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Removes an event (Admin only), unless a register has been taken at it.</summary>
    public async Task DeleteEventAsync(int eventId, CancellationToken ct = default)
    {
        RequireAdmin();
        var chapterEvent = await _db.ChapterEvents.SingleOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new InvalidOperationException("That event is not in this chapter's calendar.");
        if (await _db.AttendanceRegisters.AnyAsync(r => r.ChapterEventId == eventId, ct))
        {
            throw new InvalidOperationException("A register has been taken at this event, so it can't be removed.");
        }

        _db.ChapterEvents.Remove(chapterEvent);
        await _db.SaveChangesAsync(ct);
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

        return input with { Title = title, Location = location };
    }

    private async Task RequireDateFreeAsync(DateOnly date, int? exceptEventId, CancellationToken ct)
    {
        if (await _db.ChapterEvents.AnyAsync(e => e.Date == date && e.Id != exceptEventId, ct))
        {
            throw new InvalidOperationException($"There is already an event on {date:d MMMM yyyy}. A chapter meets at most once a day.");
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
