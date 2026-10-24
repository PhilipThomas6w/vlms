using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Calendar;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The liturgical calendar import (functional.md FR-022, TC-053): parsing the New Calendar feed's
/// shape as checked on 24/10/2026 (folded lines, escapes, a fasting emoji before the title, the fast
/// as its own paragraph, readings over several paragraphs), and the import's replace-what-the-feed-covers
/// behaviour, run only by the scheduled job.
/// </summary>
public sealed class LiturgicalCalendarTests : IDisposable
{
    // Three days in the feed's own shape (abridged texts; line endings CRLF, lines folded at 75 octets).
    private const string Feed =
        "BEGIN:VCALENDAR\r\n" +
        "PRODID:-//Google Inc//Google Calendar 70.9054//EN\r\n" +
        "X-WR-CALNAME:Orthodox Feasts - English\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;VALUE=DATE:20261025\r\n" +
        "DTEND;VALUE=DATE:20261026\r\n" +
        "UID:474f41524348-456e676c697368-20261025\r\n" +
        "DESCRIPTION:Saints\\, Feasts\\, and Readings for 10/25/2026\\n\\nSaints and Fea\r\n" +
        " sts: 6th Sunday of Luke\\; Martyrs Marcian and Martyrius\\; Tabitha the Merci\r\n" +
        " ful\\n\\nTone Four\\nEothinon 10\\n\\nMatins Gospel Reading: John 21:1-14\\nAt that\r\n" +
        "  time\\, Jesus revealed Himself again.\\n\\nEpistle Reading: Galatians 2:16-20\r\n" +
        " \\nBrethren\\, knowing that a man is not justified by works.\\n\\nGospel Readin\r\n" +
        " g: Luke 8:26-39\\nAt that time\\, as Jesus arrived.\\n\\nThe demons came out.\\n\r\n" +
        "SUMMARY: 6th Sunday of Luke\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;VALUE=DATE:20261115\r\n" +
        "DESCRIPTION:Saints\\, Feasts\\, and Readings for 11/15/2026\\n\\nSaints and Feasts: 8th Sunday of Luke\\; Nativity Fast\\n\\nFast Day (Fish Allowed)\\n\\nEpistle Reading: Ephesians 2:14-22\\nBrethren\\, Christ is our peace.\\n\r\n" +
        "SUMMARY:\U0001F41F 8th Sunday of Luke\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "DTSTART;VALUE=DATE:20260403\r\n" +
        "DESCRIPTION:Saints and Feasts: Nicetas\\, Abbot of the Monastery of Medicium\\n\\nStrict Fast\\n\\nOld Testament Reading: Isaiah 66:10-24\\nRejoice with Jerusalem.\\n\r\n" +
        "SUMMARY:☦️ Nicetas\\, Abbot of the Monastery of Medicium\r\n" +
        "END:VEVENT\r\n" +
        "BEGIN:VEVENT\r\n" +
        "SUMMARY:No date, so skipped\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public LiturgicalCalendarTests()
    {
        _connectionString = $"Data Source=file:vlms-liturgical-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();
        using var db = CreateDb(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext CreateDb(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private LiturgicalCalendarImporter Importer(VlmsDbContext db, ICurrentUserContext user, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(db, user, new HttpClient(new StubHandler(body, status)), NullLogger<LiturgicalCalendarImporter>.Instance, "https://feed.example/basic.ics");

    [Fact]
    public void Parse_ReadsEachDaysFeastFastSaintsAndReadings_AndSkipsADayWithNoDate()
    {
        var days = LiturgicalFeedParser.Parse(Feed);

        Assert.Equal([new DateOnly(2026, 4, 3), new DateOnly(2026, 10, 25), new DateOnly(2026, 11, 15)], days.Select(d => d.Date));

        var sunday = days[1];
        Assert.Equal("6th Sunday of Luke", sunday.Title);
        Assert.Equal(LiturgicalFast.None, sunday.Fast);
        Assert.Equal("6th Sunday of Luke; Martyrs Marcian and Martyrius; Tabitha the Merciful", sunday.Saints);
        Assert.Equal(
            [("Matins Gospel Reading", "John 21:1-14"), ("Epistle Reading", "Galatians 2:16-20"), ("Gospel Reading", "Luke 8:26-39")],
            sunday.Readings.Select(r => (r.Heading, r.Reference)));
        Assert.Equal("Brethren, knowing that a man is not justified by works.", sunday.Readings[1].Text);
        Assert.Equal("At that time, as Jesus arrived.\n\nThe demons came out.", sunday.Readings[2].Text);
    }

    [Fact]
    public void Parse_DropsTheFastingEmojiFromTheTitle_AndReadsTheFastFromItsParagraph()
    {
        var days = LiturgicalFeedParser.Parse(Feed).ToDictionary(d => d.Date);

        Assert.Equal("8th Sunday of Luke", days[new DateOnly(2026, 11, 15)].Title);
        Assert.Equal(LiturgicalFast.Fish, days[new DateOnly(2026, 11, 15)].Fast);
        Assert.Equal("Nicetas, Abbot of the Monastery of Medicium", days[new DateOnly(2026, 4, 3)].Title);
        Assert.Equal(LiturgicalFast.Strict, days[new DateOnly(2026, 4, 3)].Fast);
    }

    [Theory]
    [InlineData("Fast Free", LiturgicalFast.FastFree)]
    [InlineData("Fast Day (Wine and Oil Allowed)", LiturgicalFast.WineAndOil)]
    [InlineData("Fast Day (Dairy\\, Eggs\\, and Fish Allowed)", LiturgicalFast.DairyEggsAndFish)]
    [InlineData("Tone Four", LiturgicalFast.None)]
    public void Parse_KnowsEachFastTheFeedStates(string line, LiturgicalFast expected)
    {
        var ics = "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20261201\r\nDESCRIPTION:Saints and Feasts: A\\n\\n" + line +
                  "\\n\\nGospel Reading: Luke 1:1\\nText.\r\nSUMMARY:A\r\nEND:VEVENT\r\n";

        Assert.Equal(expected, Assert.Single(LiturgicalFeedParser.Parse(ics)).Fast);
    }

    [Fact]
    public async Task Import_StoresEveryDay_AndAgainReplacesThemWithWhatTheFeedNowSays()
    {
        using (var db = CreateDb(SystemCurrentUserContext.Instance))
        {
            Assert.Equal(3, await Importer(db, SystemCurrentUserContext.Instance, Feed).ImportAsync());
        }

        using (var db = CreateDb(SystemCurrentUserContext.Instance))
        {
            var corrected = Feed.Replace("SUMMARY: 6th Sunday of Luke", "SUMMARY: Sixth Sunday of Luke");
            await Importer(db, SystemCurrentUserContext.Instance, corrected).ImportAsync();
        }

        using var read = CreateDb(TestSeedUserContext.Instance);
        Assert.Equal(3, read.LiturgicalDays.Count());
        Assert.Equal("Sixth Sunday of Luke", read.LiturgicalDays.Single(d => d.Date == new DateOnly(2026, 10, 25)).Title);
        Assert.Equal(5, read.LiturgicalReadings.Count());
    }

    [Fact]
    public async Task Import_OfAnEmptyOrFailedFeed_ChangesNothing()
    {
        using (var db = CreateDb(SystemCurrentUserContext.Instance))
        {
            await Importer(db, SystemCurrentUserContext.Instance, Feed).ImportAsync();
        }

        using (var db = CreateDb(SystemCurrentUserContext.Instance))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Importer(db, SystemCurrentUserContext.Instance, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n").ImportAsync());
            await Assert.ThrowsAsync<HttpRequestException>(() => Importer(db, SystemCurrentUserContext.Instance, "", HttpStatusCode.InternalServerError).ImportAsync());
        }

        using var read = CreateDb(TestSeedUserContext.Instance);
        Assert.Equal(3, read.LiturgicalDays.Count());
    }

    [Fact]
    public async Task Import_IsRefused_ForAnyoneButTheScheduledJob()
    {
        var admin = new FakeCurrentUserContext(1, Role.Admin, Role.NationalAdministrator) { ActiveChapterId = Chapter.ShrewsburyId };
        using var db = CreateDb(admin);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Importer(db, admin, Feed).ImportAsync());
    }

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
