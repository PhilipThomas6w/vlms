using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vlms.Domain;

namespace Vlms.Infrastructure.Calendar;

/// <summary>One reading of a parsed day: its heading ("Epistle Reading"), reference ("Galatians 2:16-20") and text.</summary>
public sealed record ParsedReading(string Heading, string Reference, string Text);

/// <summary>One day of the liturgical feed, parsed.</summary>
public sealed record ParsedLiturgicalDay(DateOnly Date, string Title, LiturgicalFast Fast, string Saints, IReadOnlyList<ParsedReading> Readings);

/// <summary>
/// Parses the New Calendar feed (functional.md FR-022): the Greek Orthodox Archdiocese of America's
/// "Orthodox Feasts - English" ICS calendar, checked 24/10/2026. One all-day VEVENT per day:
/// <list type="bullet">
/// <item>SUMMARY is the main feast, sometimes after a fasting emoji, which is dropped (the fast is read
/// from the description instead);</item>
/// <item>DESCRIPTION is paragraphs: a "Saints, Feasts, and Readings for ..." heading; "Saints and
/// Feasts: a; b; c"; sometimes the tone; the fast as one paragraph ("Strict Fast", "Fast Day (Fish
/// Allowed)", "Fast Free" and so on; none on an ordinary day); then each reading as "Epistle Reading:
/// Galatians 2:16-20" followed by its text, which may run over several paragraphs.</item>
/// </list>
/// Handles RFC 5545 line folding and text escapes only as far as this feed needs; anything it does
/// not recognise is skipped, never guessed at.
/// </summary>
public static partial class LiturgicalFeedParser
{
    private static readonly Dictionary<string, LiturgicalFast> FastLines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fast Free"] = LiturgicalFast.FastFree,
        ["Fast Day (Wine and Oil Allowed)"] = LiturgicalFast.WineAndOil,
        ["Fast Day (Fish Allowed)"] = LiturgicalFast.Fish,
        ["Fast Day (Dairy, Eggs, and Fish Allowed)"] = LiturgicalFast.DairyEggsAndFish,
        ["Strict Fast"] = LiturgicalFast.Strict,
    };

    public static IReadOnlyList<ParsedLiturgicalDay> Parse(string ics)
    {
        ArgumentNullException.ThrowIfNull(ics);
        var days = new Dictionary<DateOnly, ParsedLiturgicalDay>();
        Dictionary<string, string>? current = null;

        foreach (var line in Unfold(ics))
        {
            if (line == "BEGIN:VEVENT")
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line == "END:VEVENT")
            {
                if (current is not null && ToDay(current) is { } day)
                {
                    days[day.Date] = day;
                }

                current = null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            // "DTSTART;VALUE=DATE" -> "DTSTART"; parameters are not needed here.
            var name = line[..colon].Split(';')[0];
            current[name] = line[(colon + 1)..];
        }

        return days.Values.OrderBy(d => d.Date).ToList();
    }

    private static ParsedLiturgicalDay? ToDay(Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue("DTSTART", out var start)
            || !DateOnly.TryParseExact(start.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        var title = StripLeadingSymbols(Unescape(properties.GetValueOrDefault("SUMMARY") ?? ""));
        if (title.Length == 0)
        {
            return null;
        }

        var description = Unescape(properties.GetValueOrDefault("DESCRIPTION") ?? "").Replace("\r\n", "\n");
        var paragraphs = ParagraphBreak().Split(description).Select(p => p.Trim('\n', ' ')).Where(p => p.Length > 0).ToList();

        var saints = "";
        var fast = LiturgicalFast.None;
        var readings = new List<ParsedReading>();
        string? heading = null, reference = null;
        var text = new List<string>();

        void EndReading()
        {
            if (heading is not null)
            {
                readings.Add(new ParsedReading(heading, reference!, string.Join("\n\n", text).Trim()));
            }

            heading = null;
            reference = null;
            text.Clear();
        }

        foreach (var paragraph in paragraphs)
        {
            var firstLine = paragraph.Split('\n')[0].Trim();
            var readingHeading = ReadingHeading().Match(firstLine);
            if (readingHeading.Success)
            {
                EndReading();
                heading = readingHeading.Groups["heading"].Value.Trim();
                reference = readingHeading.Groups["reference"].Value.Trim();
                var rest = paragraph.Contains('\n') ? paragraph[(paragraph.IndexOf('\n') + 1)..].Trim() : "";
                if (rest.Length > 0)
                {
                    text.Add(rest);
                }

                continue;
            }

            if (heading is not null)
            {
                text.Add(paragraph);
                continue;
            }

            if (firstLine.StartsWith("Saints and Feasts:", StringComparison.OrdinalIgnoreCase))
            {
                saints = firstLine["Saints and Feasts:".Length..].Trim().TrimEnd(';').Trim();
            }
            else if (FastLines.TryGetValue(firstLine, out var stated))
            {
                fast = stated;
            }
        }

        EndReading();
        return new ParsedLiturgicalDay(date, title, fast, saints, readings);
    }

    /// <summary>Joins folded lines (a line break followed by a space or tab continues the line).</summary>
    private static IEnumerable<string> Unfold(string ics)
    {
        var builder = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                builder.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
            }

            builder.Clear().Append(raw);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>RFC 5545 text escapes: \n or \N is a line break; \, \; and \\ are the character itself.</summary>
    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    // The feed puts a fasting emoji (and a variation selector) before some titles.
    private static string StripLeadingSymbols(string title)
    {
        var start = 0;
        while (start < title.Length && !char.IsLetterOrDigit(title, start))
        {
            start += char.IsSurrogatePair(title, start) ? 2 : 1;
        }

        return title[start..].Trim();
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"^(?<heading>[A-Za-z' ]*Readings?):\s*(?<reference>.+)$")]
    private static partial Regex ReadingHeading();
}
