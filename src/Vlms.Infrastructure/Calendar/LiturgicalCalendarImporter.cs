using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;

namespace Vlms.Infrastructure.Calendar;

/// <summary>
/// Imports the liturgical calendar (functional.md FR-022) from the New Calendar feed into
/// <see cref="LiturgicalDay"/> and <see cref="LiturgicalReading"/>: run by Vlms.Jobs on its daily
/// timer. Each run replaces every day the feed covers with what the feed now says, so corrections
/// published upstream are picked up; days outside the feed's range are kept. A download that fails,
/// or a feed with no days in it, changes nothing. National data: the same for every chapter, and
/// written only here, by the scheduled job's system principal.
/// </summary>
public sealed class LiturgicalCalendarImporter
{
    /// <summary>The feed Philip chose, 24/10/2026: GOARCH's "Orthodox Feasts - English" calendar.</summary>
    public const string DefaultFeedUrl =
        "https://calendar.google.com/calendar/ical/i0foh8u5am8ui8grpo1svvaun4%40group.calendar.google.com/public/basic.ics";

    /// <summary>Configuration key for the feed's address.</summary>
    public const string FeedUrlKey = "Calendar:LiturgicalFeedUrl";

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly HttpClient _http;
    private readonly ILogger<LiturgicalCalendarImporter> _logger;
    private readonly string _feedUrl;

    public LiturgicalCalendarImporter(
        VlmsDbContext db, ICurrentUserContext currentUser, HttpClient http, ILogger<LiturgicalCalendarImporter> logger, string? feedUrl)
    {
        _db = db;
        _currentUser = currentUser;
        _http = http;
        _logger = logger;
        _feedUrl = string.IsNullOrWhiteSpace(feedUrl) ? DefaultFeedUrl : feedUrl;
    }

    /// <summary>Downloads and stores the feed; returns the number of days imported.</summary>
    public async Task<int> ImportAsync(CancellationToken ct = default)
    {
        if (!_currentUser.SeesAllChapters)
        {
            throw new UnauthorizedAccessException("Only the scheduled job imports the liturgical calendar.");
        }

        var ics = await _http.GetStringAsync(_feedUrl, ct);
        var days = LiturgicalFeedParser.Parse(ics);
        if (days.Count == 0)
        {
            throw new InvalidOperationException("The liturgical calendar feed had no days in it; nothing was changed.");
        }

        var first = days[0].Date;
        var last = days[^1].Date;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.LiturgicalReadings.Where(r => r.Date >= first && r.Date <= last).ExecuteDeleteAsync(ct);
        await _db.LiturgicalDays.Where(d => d.Date >= first && d.Date <= last).ExecuteDeleteAsync(ct);

        var nextReadingId = await _db.NextIdAsync<LiturgicalReading>(ct);
        foreach (var day in days)
        {
            var row = new LiturgicalDay { Date = day.Date, Title = day.Title, Fast = day.Fast, Saints = day.Saints };
            var order = 0;
            foreach (var reading in day.Readings)
            {
                row.Readings.Add(new LiturgicalReading
                {
                    Id = nextReadingId++,
                    Date = day.Date,
                    Order = ++order,
                    Heading = reading.Heading,
                    Reference = reading.Reference,
                    Text = reading.Text,
                });
            }

            _db.LiturgicalDays.Add(row);
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _db.ChangeTracker.Clear();

        _logger.LogInformation("Liturgical calendar imported: {DayCount} day(s), {First:yyyy-MM-dd} to {Last:yyyy-MM-dd}.", days.Count, first, last);
        return days.Count;
    }
}
