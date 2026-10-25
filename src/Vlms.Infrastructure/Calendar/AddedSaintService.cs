using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Calendar;

/// <summary>An added saint as listed on the added saints page.</summary>
public sealed record AddedSaintItem(int Id, int Month, int Day, string Name);

/// <summary>
/// One row of a .csv file being imported, as previewed before anything is saved: the date and name as
/// written, the date read from it, why the row can't be added (<see cref="Error"/>), whether it is
/// already added, and the saints the liturgical calendar lists on that date, with
/// <see cref="InFeed"/> set when they include this name (so it would be hidden there anyway).
/// </summary>
public sealed record SaintImportRow(
    int Line, string Date, string Name, int? Month, int? Day, string? Error, bool AlreadyAdded,
    IReadOnlyList<string> FeedSaints, bool InFeed)
{
    /// <summary>Whether the row can be saved: it has no error and is not already added.</summary>
    public bool CanAdd => Error is null && !AlreadyAdded;
}

/// <summary>A saint to save from an import: a month, a day and a name.</summary>
public sealed record SaintToAdd(int Month, int Day, string Name);

/// <summary>What an import saved: the saints added, and those skipped as already added.</summary>
public sealed record SaintImportResult(int Added, int Skipped);

/// <summary>
/// British (including Welsh) and Irish saints added to the saints of the day (functional.md FR-022;
/// Philip, 25/10/2026). National: added by a National Administrator with two-factor authentication,
/// checked inside the service, one by one or imported from a .csv file (columns <c>date</c>, DD/MM
/// repeating yearly, and <c>name</c>). An import is previewed first, each row with its errors and that
/// date's saints from the liturgical calendar, and saves only the rows chosen; rows already added
/// (same date and name) are skipped. An import never replaces or removes anything. The calendar
/// (<see cref="CalendarService.GetLiturgicalDayAsync"/>) shows added saints after the feed's, and
/// hides one the feed already lists on that day (<see cref="SameName"/>).
/// </summary>
public sealed class AddedSaintService
{
    public const int NameMaxLength = 200;

    /// <summary>The largest .csv file an import reads.</summary>
    public const int MaxImportBytes = 256 * 1024;

    /// <summary>The most rows one import reads.</summary>
    public const int MaxImportRows = 2000;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public AddedSaintService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Whether two saints' names are the same: exactly, apart from letter case and surrounding spaces.</summary>
    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every added saint, in calendar order (1 January first), then by name.</summary>
    public async Task<IReadOnlyList<AddedSaintItem>> ListAsync(CancellationToken ct = default)
    {
        RequireNationalAdministrator();

        return await _db.AddedSaints.AsNoTracking()
            .OrderBy(s => s.Month).ThenBy(s => s.Day).ThenBy(s => s.Name)
            .Select(s => new AddedSaintItem(s.Id, s.Month, s.Day, s.Name))
            .ToListAsync(ct);
    }

    /// <summary>Adds <paramref name="name"/> on <paramref name="day"/>/<paramref name="month"/> every year. Refused if already added there.</summary>
    public async Task<int> AddAsync(int month, int day, string name, CancellationToken ct = default)
    {
        var userId = RequireNationalAdministrator();
        var error = ValidateDate(month, day) ?? ValidateName(name);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        var clean = name.Trim();
        var existing = await _db.AddedSaints.AsNoTracking()
            .Where(s => s.Month == month && s.Day == day)
            .Select(s => s.Name)
            .ToListAsync(ct);
        if (existing.Any(n => SameName(n, clean)))
        {
            throw new InvalidOperationException($"{clean} is already added on {DateLabel(month, day)}.");
        }

        var id = await _db.NextIdAsync<AddedSaint>(ct);
        _db.AddedSaints.Add(new AddedSaint { Id = id, Month = month, Day = day, Name = clean, AddedByUserId = userId, AddedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Removes an added saint from its date.</summary>
    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        RequireNationalAdministrator();

        var saint = await _db.AddedSaints.SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException("That saint is not in the list.");
        _db.AddedSaints.Remove(saint);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads a .csv file and previews each row: errors, whether it is already added (or repeats an earlier
    /// row), and that date's saints in the liturgical calendar this year (29 February: the next leap year).
    /// Saves nothing. Refuses a file larger than <see cref="MaxImportBytes"/> or with more than
    /// <see cref="MaxImportRows"/> rows, or one that is not a two-column .csv.
    /// </summary>
    public async Task<IReadOnlyList<SaintImportRow>> PreviewImportAsync(string csv, CancellationToken ct = default)
    {
        RequireNationalAdministrator();

        if (Encoding.UTF8.GetByteCount(csv) > MaxImportBytes)
        {
            throw new ArgumentException("The file is larger than 256 KB.");
        }

        var records = ParseCsv(csv);
        if (records.Count > 0 && records[0].Fields.Count > 0 && records[0].Fields[0].Trim().Equals("date", StringComparison.OrdinalIgnoreCase))
        {
            records = records.Skip(1).ToList();
        }

        if (records.Count == 0)
        {
            throw new ArgumentException("The file has no saints in it. It needs two columns: date (DD/MM) and name.");
        }

        if (records.Count > MaxImportRows)
        {
            throw new ArgumentException($"The file has more than {MaxImportRows} rows. Split it into smaller files.");
        }

        var added = (await _db.AddedSaints.AsNoTracking().Select(s => new { s.Month, s.Day, s.Name }).ToListAsync(ct))
            .ToLookup(s => (s.Month, s.Day), s => s.Name);
        var feedDates = new HashSet<DateOnly>();
        var parsed = new List<(CsvRecord Record, string Date, string Name, int? Month, int? Day, string? Error)>();
        foreach (var record in records)
        {
            var date = record.Fields.Count > 0 ? record.Fields[0].Trim() : "";
            var name = record.Fields.Count > 1 ? record.Fields[1].Trim() : "";
            string? error = record.Fields.Count != 2 ? "Expected two columns: date and name." : null;
            int? month = null, day = null;
            if (TryParseDate(date, out var m, out var d))
            {
                month = m;
                day = d;
                feedDates.Add(FeedDate(m, d));
            }
            else
            {
                error ??= $"\"{date}\" is not a date written DD/MM, such as 01/03.";
            }

            error ??= ValidateName(name);
            parsed.Add((record, date, name, month, day, error));
        }

        var feed = await _db.LiturgicalDays.AsNoTracking()
            .Where(d => feedDates.Contains(d.Date))
            .Select(d => new { d.Date, d.Saints })
            .ToDictionaryAsync(d => d.Date, d => SplitSaints(d.Saints), ct);

        var seen = new Dictionary<(int, int, string), int>();
        var rows = new List<SaintImportRow>();
        foreach (var (record, date, name, month, day, firstError) in parsed)
        {
            var error = firstError;
            IReadOnlyList<string> feedSaints = [];
            var alreadyAdded = false;
            if (month is int m && day is int d)
            {
                feedSaints = feed.GetValueOrDefault(FeedDate(m, d)) ?? [];
                if (error is null)
                {
                    alreadyAdded = added[(m, d)].Any(n => SameName(n, name));
                    var key = (m, d, name.ToUpperInvariant());
                    if (seen.TryGetValue(key, out var line))
                    {
                        error = $"Repeats line {line}.";
                    }
                    else
                    {
                        seen[key] = record.Line;
                    }
                }
            }

            rows.Add(new SaintImportRow(record.Line, date, name, month, day, error, alreadyAdded, feedSaints,
                feedSaints.Any(s => SameName(s, name))));
        }

        return rows;
    }

    /// <summary>
    /// Saves the chosen rows of a previewed import. Each is checked again; one already added (same date
    /// and name), or repeated in <paramref name="saints"/>, is skipped. Nothing is replaced or removed.
    /// </summary>
    public async Task<SaintImportResult> ImportAsync(IReadOnlyList<SaintToAdd> saints, CancellationToken ct = default)
    {
        var userId = RequireNationalAdministrator();
        if (saints.Count > MaxImportRows)
        {
            throw new ArgumentException($"An import adds at most {MaxImportRows} saints.");
        }

        foreach (var saint in saints)
        {
            if ((ValidateDate(saint.Month, saint.Day) ?? ValidateName(saint.Name)) is { } error)
            {
                throw new ArgumentException($"{saint.Name}: {error}");
            }
        }

        var existing = (await _db.AddedSaints.AsNoTracking().Select(s => new { s.Month, s.Day, s.Name }).ToListAsync(ct))
            .Select(s => (s.Month, s.Day, s.Name.Trim().ToUpperInvariant()))
            .ToHashSet();
        var id = await _db.NextIdAsync<AddedSaint>(ct);
        var now = DateTime.UtcNow;
        var addedCount = 0;
        foreach (var saint in saints)
        {
            var name = saint.Name.Trim();
            if (!existing.Add((saint.Month, saint.Day, name.ToUpperInvariant())))
            {
                continue;
            }

            _db.AddedSaints.Add(new AddedSaint { Id = id++, Month = saint.Month, Day = saint.Day, Name = name, AddedByUserId = userId, AddedAt = now });
            addedCount++;
        }

        await _db.SaveChangesAsync(ct);
        return new SaintImportResult(addedCount, saints.Count - addedCount);
    }

    /// <summary>Reads a date written D/M or DD/MM (01/03 is 1 March). Any day that exists in a leap year is accepted.</summary>
    public static bool TryParseDate(string text, out int month, out int day)
    {
        month = 0;
        day = 0;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || ValidateDate(m, d) is not null)
        {
            return false;
        }

        month = m;
        day = d;
        return true;
    }

    /// <summary>A date as shown on the page, for example "1 March".</summary>
    public static string DateLabel(int month, int day) =>
        new DateOnly(2024, month, day).ToString("d MMMM", CultureInfo.GetCultureInfo("en-GB"));

    internal static IReadOnlyList<string> SplitSaints(string saints) =>
        saints.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ValidateDate(int month, int day) =>
        month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2024, month)
            ? "Choose a date that exists."
            : null;

    private static string? ValidateName(string? name)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0)
        {
            return "Give the saint's name.";
        }

        if (clean.Length > NameMaxLength)
        {
            return $"The name is longer than {NameMaxLength} characters.";
        }

        return clean.Any(char.IsControl) ? "The name has a character that can't be shown." : null;
    }

    // The date this year whose feed saints a preview shows; 29 February in the next leap year.
    private static DateOnly FeedDate(int month, int day)
    {
        var year = DateTime.UtcNow.Year;
        while (day > DateTime.DaysInMonth(year, month))
        {
            year++;
        }

        return new DateOnly(year, month, day);
    }

    private sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

    // RFC 4180: comma-separated fields, double quotes around a field holding commas, quotes or line
    // breaks, "" for a quote inside one; CRLF or LF line ends; a leading byte order mark ignored; blank
    // lines skipped. Line is where the record starts, for the preview.
    private static List<CsvRecord> ParseCsv(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        void EndRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Count > 1 || fields[0].Trim().Length > 0)
            {
                records.Add(new CsvRecord(recordLine, fields.ToList()));
            }

            fields.Clear();
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }

        return records;
    }

    private int RequireNationalAdministrator()
    {
        if (!_currentUser.HasRole(Role.NationalAdministrator) || !_currentUser.HasMfaEnabled)
        {
            throw new UnauthorizedAccessException(
                "Only a National Administrator with two-factor authentication turned on can add saints.");
        }

        return _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
    }
}
