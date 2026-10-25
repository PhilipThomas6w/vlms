using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Calendar;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Added British and Irish saints (functional.md FR-022, TC-056): only a National Administrator with
/// two-factor authentication adds, imports or removes them; they show in every chapter after the
/// liturgical calendar's saints, hidden where it lists the same name; a .csv import is previewed
/// (errors, already added, that date's calendar saints) and saves only the chosen rows, skipping any
/// already added and never removing one.
/// </summary>
public sealed class AddedSaintServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int NationalAdmin = 900;
    private const int Admin = 11;
    private const int CoventryParent = 21;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public AddedSaintServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-saints-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = CreateDb(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        AddUser(db, NationalAdmin, (Role.NationalAdministrator, null));
        AddUser(db, Admin, (Role.Admin, Chapter.ShrewsburyId));
        AddUser(db, CoventryParent, (Role.Parent, Coventry));
        db.LiturgicalDays.Add(new LiturgicalDay { Date = Today, Title = "Feast of the day", Fast = LiturgicalFast.None, Saints = "Saint A; David of Wales" });
        db.LiturgicalDays.Add(new LiturgicalDay { Date = Today.AddYears(-1), Title = "Last year", Fast = LiturgicalFast.None, Saints = "Saint A" });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext CreateDb(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private static void AddUser(VlmsDbContext db, int id, params (Role Role, int? Chapter)[] roles)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = $"user{id}@example.com" });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    private static FakeCurrentUserContext National(bool mfa = true) =>
        new(NationalAdmin, mfa, Role.NationalAdministrator) { ActiveChapterId = null };

    private async Task<T> Saints<T>(ICurrentUserContext user, Func<AddedSaintService, Task<T>> action)
    {
        using var db = CreateDb(user);
        return await action(new AddedSaintService(db, user));
    }

    private async Task<IReadOnlyList<string>> SaintsOn(DateOnly date, ICurrentUserContext reader)
    {
        using var db = CreateDb(reader);
        var detail = await new CalendarService(db, reader).GetLiturgicalDayAsync(date);
        return detail!.Saints;
    }

    [Fact]
    public async Task OnlyANationalAdministrator_WithTwoFactor_ManagesAddedSaints()
    {
        var admin = new FakeCurrentUserContext(Admin, Role.Admin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Saints(admin, s => s.ListAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Saints(admin, s => s.AddAsync(3, 1, "David")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Saints(admin, s => s.PreviewImportAsync("01/03,David")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Saints(admin, s => s.ImportAsync([new SaintToAdd(3, 1, "David")])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Saints(National(mfa: false), s => s.AddAsync(3, 1, "David")));

        var id = await Saints(National(), s => s.AddAsync(3, 1, "David"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await Saints(admin, async s => { await s.RemoveAsync(id); return 0; });
        });
        Assert.Single(await Saints(National(), s => s.ListAsync()));
    }

    [Fact]
    public async Task AddingASaint_ChecksTheDateAndName_AndRefusesTheSameNameTwiceOnADate()
    {
        await Saints(National(), s => s.AddAsync(5, 27, "Melangell"));
        await Saints(National(), s => s.AddAsync(1, 31, "Melangell"));
        await Saints(National(), s => s.AddAsync(2, 29, "Oswald of Worcester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Saints(National(), s => s.AddAsync(5, 27, " melangell ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.AddAsync(4, 31, "Nobody")));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.AddAsync(13, 1, "Nobody")));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.AddAsync(1, 1, "  ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.AddAsync(1, 1, new string('a', 201))));

        var list = await Saints(National(), s => s.ListAsync());
        Assert.Equal(["Melangell", "Oswald of Worcester", "Melangell"], list.Select(s => s.Name));
        Assert.Equal([(1, 31), (2, 29), (5, 27)], list.Select(s => (s.Month, s.Day)));
    }

    [Fact]
    public async Task AddedSaints_ShowInEveryChapter_AfterTheCalendars_ExceptWhereItListsTheSameName()
    {
        await Saints(National(), s => s.AddAsync(Today.Month, Today.Day, "Winefride"));
        await Saints(National(), s => s.AddAsync(Today.Month, Today.Day, "david of wales"));

        var shrewsbury = new FakeCurrentUserContext(Admin, Role.Admin);
        Assert.Equal(["Saint A", "David of Wales", "Winefride"], await SaintsOn(Today, shrewsbury));

        var coventry = new FakeCurrentUserContext(CoventryParent, Role.Parent) { ActiveChapterId = Coventry };
        Assert.Equal(["Saint A", "David of Wales", "Winefride"], await SaintsOn(Today, coventry));

        // Another year's calendar does not list David, so there the added saint shows.
        Assert.Equal(["Saint A", "david of wales", "Winefride"], await SaintsOn(Today.AddYears(-1), shrewsbury));
    }

    [Fact]
    public async Task AnImportPreview_ShowsEachRowsErrors_AndTheCalendarsSaints_AndSavesNothing()
    {
        await Saints(National(), s => s.AddAsync(5, 27, "Melangell"));
        var todayText = Today.ToString("dd/MM");
        var csv = "﻿date,name\r\n"
            + "27/05,Melangell\r\n"
            + "1/3,\"David, Archbishop of Menevia\"\r\n"
            + "31/04,Nobody\r\n"
            + "02/03,\r\n"
            + "03/03,Too,Many\r\n"
            + "\r\n"
            + "1/3,\"David, Archbishop of Menevia\"\r\n"
            + $"{todayText},David of Wales\r\n"
            + $"{todayText},\"Said \"\"the Wise\"\"\"\n";

        var rows = await Saints(National(), s => s.PreviewImportAsync(csv));

        Assert.Equal([2, 3, 4, 5, 6, 8, 9, 10], rows.Select(r => r.Line));
        Assert.True(rows[0].AlreadyAdded);
        Assert.False(rows[0].CanAdd);
        Assert.Equal(("David, Archbishop of Menevia", 3, 1), (rows[1].Name, rows[1].Month!.Value, rows[1].Day!.Value));
        Assert.True(rows[1].CanAdd);
        Assert.Contains("not a date", rows[2].Error);
        Assert.Equal("Give the saint's name.", rows[3].Error);
        Assert.Equal("Expected two columns: date and name.", rows[4].Error);
        Assert.Equal("Repeats line 3.", rows[5].Error);
        Assert.True(rows[6].InFeed);
        Assert.Equal(["Saint A", "David of Wales"], rows[6].FeedSaints);
        Assert.True(rows[6].CanAdd);
        Assert.Equal("Said \"the Wise\"", rows[7].Name);
        Assert.False(rows[7].InFeed);

        Assert.Single(await Saints(National(), s => s.ListAsync()));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.PreviewImportAsync("date,name\r\n")));
        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.PreviewImportAsync(new string('x', AddedSaintService.MaxImportBytes + 1))));
    }

    [Fact]
    public async Task AnImport_AddsTheChosenRows_SkipsThoseAlreadyAdded_AndRemovesNothing()
    {
        await Saints(National(), s => s.AddAsync(5, 27, "Melangell"));
        await Saints(National(), s => s.AddAsync(8, 5, "Oswald"));

        var result = await Saints(National(), s => s.ImportAsync(
            [new SaintToAdd(5, 27, "Melangell"), new SaintToAdd(3, 1, "David"), new SaintToAdd(3, 1, "david"), new SaintToAdd(1, 31, "Melangell")]));

        Assert.Equal(new SaintImportResult(2, 2), result);
        var names = (await Saints(National(), s => s.ListAsync())).Select(s => (s.Month, s.Day, s.Name));
        Assert.Equal([(1, 31, "Melangell"), (3, 1, "David"), (5, 27, "Melangell"), (8, 5, "Oswald")], names);

        await Assert.ThrowsAsync<ArgumentException>(() => Saints(National(), s => s.ImportAsync([new SaintToAdd(2, 30, "Nobody")])));
    }

    [Fact]
    public async Task TheStartingList_ImportsWithNoErrors()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var csv = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "seed", "british-saints.csv"));

        var rows = await Saints(National(), s => s.PreviewImportAsync(csv));

        Assert.Equal(128, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Error));
    }

    [Theory]
    [InlineData("01/03", true, 3, 1)]
    [InlineData("1/3", true, 3, 1)]
    [InlineData("29/02", true, 2, 29)]
    [InlineData("30/02", false, 0, 0)]
    [InlineData("03-01", false, 0, 0)]
    [InlineData("01/03/2026", false, 0, 0)]
    [InlineData("+1/3", false, 0, 0)]
    public void Dates_AreWrittenDayThenMonth(string text, bool ok, int month, int day)
    {
        Assert.Equal(ok, AddedSaintService.TryParseDate(text, out var m, out var d));
        Assert.Equal((month, day), (m, d));
    }
}
