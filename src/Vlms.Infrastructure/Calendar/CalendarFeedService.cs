using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Infrastructure.Calendar;

/// <summary>
/// Each person's private link to subscribe to their chapter's events in their own calendar app
/// (functional.md FR-022).
///
/// <b>The link.</b> A 256-bit random secret in the address; only its SHA-256 hash is stored
/// (<see cref="CalendarFeedToken"/>), so the full link is shown once, when made, and making a new one
/// replaces the old (and stops it working). Anyone who can read the chapter's calendar can make one
/// for the chapter they are working in.
///
/// <b>The feed.</b> Calendar apps fetch it without signing in, so <see cref="BuildFeedAsync"/> finds the
/// link by its hash, checks that its owner still belongs to the link's chapter
/// (<see cref="ChapterMembership.ChapterIds"/>: a staff leaver, or someone whose roles there were all
/// removed, gets nothing), and then reads that chapter's events through a context whose only standing
/// is that chapter (<see cref="CalendarFeedUserContext"/>: no user, no roles, so the sensitive-data
/// filters show it nothing). That context is created here, used for the one query of events, and
/// disposed. The feed holds events only (date, name, times, place), never a child's or anyone's
/// personal data.
///
/// <b>The chapter's public link</b> (<see cref="ChapterPublicFeed"/>, Philip 25/10/2026): one per
/// chapter, made, replaced or stopped by its Admins and shown to everyone in the chapter, carrying its
/// parish events only (services, parish meetings), never a class or excursion, whose dates and places
/// say where children will be.
/// </summary>
public sealed class CalendarFeedService
{
    /// <summary>How far back the feed goes.</summary>
    public const int FeedMonthsBack = 12;

    private static readonly TimeZoneInfo UkTime = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public CalendarFeedService(VlmsDbContext db, ICurrentUserContext currentUser, DbContextOptions<VlmsDbContext> options)
    {
        _db = db;
        _currentUser = currentUser;
        _options = options;
    }

    /// <summary>When the caller's link for their active chapter was made, or null if they have none.</summary>
    public async Task<DateTime?> GetLinkCreatedAtAsync(CancellationToken ct = default)
    {
        var (userId, chapterId) = RequireReader();
        return await _db.CalendarFeedTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.ChapterId == chapterId)
            .Select(t => (DateTime?)t.CreatedAt)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Makes a new link for the caller's active chapter, replacing any earlier one; returns its secret, shown once.</summary>
    public async Task<string> CreateLinkAsync(CancellationToken ct = default)
    {
        var (userId, chapterId) = RequireReader();
        _db.CalendarFeedTokens.RemoveRange(_db.CalendarFeedTokens.Where(t => t.UserId == userId && t.ChapterId == chapterId));
        await _db.SaveChangesAsync(ct);

        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        _db.CalendarFeedTokens.Add(new CalendarFeedToken
        {
            Id = await _db.NextIdAsync<CalendarFeedToken>(ct),
            UserId = userId,
            ChapterId = chapterId,
            TokenHash = Hash(secret),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>Stops the caller's link for their active chapter working.</summary>
    public async Task RemoveLinkAsync(CancellationToken ct = default)
    {
        var (userId, chapterId) = RequireReader();
        _db.CalendarFeedTokens.RemoveRange(_db.CalendarFeedTokens.Where(t => t.UserId == userId && t.ChapterId == chapterId));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The key of the active chapter's public link to its parish events, or null if it has none. Anyone in the chapter.</summary>
    public async Task<string?> GetPublicLinkKeyAsync(CancellationToken ct = default)
    {
        var (_, chapterId) = RequireReader();
        return await _db.ChapterPublicFeeds.AsNoTracking()
            .Where(f => f.ChapterId == chapterId)
            .Select(f => f.Key)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Makes the active chapter's public link, replacing any earlier one (which stops working); returns its key. Admin only.</summary>
    public async Task<string> CreatePublicLinkAsync(CancellationToken ct = default)
    {
        var (userId, chapterId) = RequireAdmin();
        _db.ChapterPublicFeeds.RemoveRange(_db.ChapterPublicFeeds.Where(f => f.ChapterId == chapterId));
        await _db.SaveChangesAsync(ct);

        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        _db.ChapterPublicFeeds.Add(new ChapterPublicFeed { ChapterId = chapterId, Key = key, CreatedAt = DateTime.UtcNow, CreatedByUserId = userId });
        await _db.SaveChangesAsync(ct);
        return key;
    }

    /// <summary>Stops the active chapter's public link working. Admin only.</summary>
    public async Task RemovePublicLinkAsync(CancellationToken ct = default)
    {
        var (_, chapterId) = RequireAdmin();
        _db.ChapterPublicFeeds.RemoveRange(_db.ChapterPublicFeeds.Where(f => f.ChapterId == chapterId));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The iCalendar text for the public link with key <paramref name="key"/>: its chapter's parish
    /// events (services, parish meetings) from <see cref="FeedMonthsBack"/> months ago, never a class or
    /// excursion. Null if no such link exists or the chapter is closed. Needs no signed-in caller.
    /// </summary>
    public async Task<string?> BuildPublicFeedAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        {
            return null;
        }

        var feed = await _db.ChapterPublicFeeds.AsNoTracking().SingleOrDefaultAsync(f => f.Key == key, ct);
        var chapter = feed is null
            ? null
            : await _db.Chapters.AsNoTracking().Where(c => c.Id == feed.ChapterId && c.IsActive).Select(c => new { c.Id, c.Name }).SingleOrDefaultAsync(ct);
        if (chapter is null)
        {
            return null;
        }

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-FeedMonthsBack);
        await using var asFeed = new VlmsDbContext(_options, new CalendarFeedUserContext(chapter.Id));
        var events = await asFeed.ChapterEvents.AsNoTracking()
            .Where(e => e.Date >= since && (e.Kind == ChapterEventKind.Service || e.Kind == ChapterEventKind.ParishMeeting))
            .OrderBy(e => e.Date)
            .ToListAsync(ct);

        return ToICalendar(chapter.Name, events, DateTime.UtcNow);
    }

    /// <summary>
    /// The iCalendar text for the link with secret <paramref name="secret"/>, or null if no such link
    /// exists or its owner no longer belongs to its chapter. Needs no signed-in caller.
    /// </summary>
    public async Task<string?> BuildFeedAsync(string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 100)
        {
            return null;
        }

        var hash = Hash(secret);
        var token = await _db.CalendarFeedTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !ChapterMembership.ChapterIds(_db, token.UserId).Contains(token.ChapterId))
        {
            return null;
        }

        var chapterName = await _db.Chapters.AsNoTracking().Where(c => c.Id == token.ChapterId).Select(c => c.Name).SingleAsync(ct);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-FeedMonthsBack);
        await using var asFeed = new VlmsDbContext(_options, new CalendarFeedUserContext(token.ChapterId));
        var events = await asFeed.ChapterEvents.AsNoTracking()
            .Where(e => e.Date >= since)
            .OrderBy(e => e.Date)
            .ToListAsync(ct);

        return ToICalendar(chapterName, events, DateTime.UtcNow);
    }

    internal static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static string ToICalendar(string chapterName, IEnumerable<ChapterEvent> events, DateTime nowUtc)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Varangians//Calendar//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:" + Escape(chapterName),
        };

        var stamp = nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in events)
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add($"UID:event-{e.Id}@varangians.co.uk");
            lines.Add("DTSTAMP:" + stamp);
            if (e.StartTime is { } start)
            {
                lines.Add("DTSTART:" + Utc(e.Date, start));
                if (e.EndTime is { } end)
                {
                    lines.Add("DTEND:" + Utc(e.Date, end));
                }
            }
            else
            {
                lines.Add("DTSTART;VALUE=DATE:" + e.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                lines.Add("DTEND;VALUE=DATE:" + e.Date.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }

            lines.Add("SUMMARY:" + Escape(e.Title));
            if (e.Location is not null)
            {
                lines.Add("LOCATION:" + Escape(e.Location));
            }

            lines.Add("END:VEVENT");
        }

        lines.Add("END:VCALENDAR");
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            Fold(builder, line);
        }

        return builder.ToString();
    }

    // Event times are UK local times; the feed gives them in UTC so no time zone block is needed.
    private static string Utc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), UkTime)
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    // RFC 5545: lines of at most 75 octets, continued with CRLF and a space; never splitting a character.
    private static void Fold(StringBuilder builder, string line)
    {
        var octets = 0;
        var limit = 75;
        for (var i = 0; i < line.Length; i++)
        {
            var length = char.IsSurrogatePair(line, i) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(i, length));
            if (octets + size > limit)
            {
                builder.Append("\r\n ");
                octets = 0;
                limit = 74;
            }

            builder.Append(line, i, length);
            octets += size;
            i += length - 1;
        }

        builder.Append("\r\n");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private (int UserId, int ChapterId) RequireReader()
    {
        var reader = new CalendarService(_db, _currentUser);
        if (!reader.CanRead || _currentUser.UserId is not int userId || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must hold a role in the chapter they are working in to subscribe to its calendar.");
        }

        return (userId, chapterId);
    }

    private (int UserId, int ChapterId) RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || _currentUser.UserId is not int userId || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must be an Admin of the chapter to manage its public calendar link.");
        }

        return (userId, chapterId);
    }
}

/// <summary>
/// The standing of a calendar subscription request (<see cref="CalendarFeedService.BuildFeedAsync"/>):
/// one chapter, whose link has been verified. No user, no roles and no MFA, so the sensitive-data
/// filters show it nothing; the chapter filters show it that chapter's rows, which is why only
/// <see cref="CalendarFeedService"/> creates it, for its one query of events. Never a request's runtime context.
/// </summary>
internal sealed class CalendarFeedUserContext(int chapterId) : ICurrentUserContext
{
    public int? UserId => null;

    public bool HasRole(Role role) => false;

    public bool HasMfaEnabled => false;

    public int? ActiveChapterId { get; } = chapterId;

    public bool SeesAllChapters => false;
}
