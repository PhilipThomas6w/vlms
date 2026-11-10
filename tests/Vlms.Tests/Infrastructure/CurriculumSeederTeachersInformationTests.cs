using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// <see cref="CurriculumSeeder"/> and docs/adr/0015 and its amendment: a lesson's Teacher's Information from
/// <c>lessons/{code}.teachers-information.md</c>, pictures in the lesson's folder under the same rules (bare names,
/// JPEG/PNG, missing is an error and nothing is written), part of the content version; no file means none; the "only
/// while the latest version came from the seeder" rule (adr/0013 decision 9); and a leftover <c>.notes.md</c> or rank-wide
/// <c>-TI.md</c> file is an error. Same temporary-folder fixture as <see cref="CurriculumSeederLessonTextTests"/>.
/// </summary>
public sealed class CurriculumSeederTeachersInformationTests : IDisposable
{
    private const string Manifest = """
        {
          "ranks": [
            { "order": 1, "code": "RECRUIT", "name": "Recruit", "source": "Recruits (1)", "lessons": [
              { "order": 1, "code": "R1-01", "title": "Intro", "tags": ["Church Life"], "source": "1. Intro" },
              { "order": 2, "code": "R1-02", "title": "Second", "tags": ["Worship"], "source": "2. Second" } ] }
          ]
        }
        """;

    private const string Text = "## Questions\n\n- Why?\n\n![The big picture](big.jpg)\n";
    private const string Notes = "## Lesson plan\n\nNOTES-PLAN\n\n![Seating plan](plan.jpg)\n\n![The big picture](big.jpg)\n";

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly string _tempRoot;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public CurriculumSeederTeachersInformationTests()
    {
        _connectionString = $"Data Source=file:vlms-seeder-information-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"vlms-seed-information-{Guid.NewGuid():N}");
        WriteText("Recruits (1)/1. Intro/Main.docx", "x");
        WriteText("Recruits (1)/2. Second/Main.docx", "x");
    }

    public void Dispose()
    {
        _anchorConnection.Dispose();
        Directory.Delete(_tempRoot, recursive: true);
    }

    private VlmsDbContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(1, Role.Admin));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        return services.BuildServiceProvider().GetRequiredService<VlmsDbContext>();
    }

    private async Task<CurriculumSeedResult> RunAsync()
    {
        var manifestPath = Path.Combine(_tempRoot, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, Manifest);
        using var db = CreateContext();
        return await new CurriculumSeeder(db, _blobStorage).SeedAsync(manifestPath, Path.Combine(_tempRoot, "content"));
    }

    private void WriteText(string relativePath, string text)
    {
        var path = Path.Combine(_tempRoot, "content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Delete(string relativePath) => File.Delete(Path.Combine(_tempRoot, "content", relativePath));

    private void WritePicture(string relativePath, SKColor colour)
    {
        var path = Path.Combine(_tempRoot, "content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
    }

    private void WriteLessonWithInformation()
    {
        WriteText("lessons/R1-01.md", Text);
        WriteText("lessons/R1-01.teachers-information.md", Notes);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        WritePicture("lessons/R1-01/plan.jpg", SKColors.Gold);
    }

    [Fact]
    public async Task TeachersInformation_IsImportedWithTheLesson_APictureOnlyItUsesMarkedStaffOnly()
    {
        WriteLessonWithInformation();

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("plan.jpg") || w.Contains("R1-01.teachers-information"));
        using var db = CreateContext();
        var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Equal((Text, Notes), (lesson.Body, lesson.TeachersInformation));
        var pictures = await db.LessonPictures.Where(p => p.LessonId == lesson.Id).OrderBy(p => p.Order).ToListAsync();
        Assert.Equal([("big.jpg", false), ("plan.jpg", true)], pictures.Select(p => (p.FileName, p.TeachersInformationOnly)));
        Assert.All(pictures, p => Assert.True(_blobStorage.TryGetBlob(p.BlobKey, out _)));
        var version = await db.LessonVersions.Include(v => v.Pictures).SingleAsync(v => v.LessonId == lesson.Id);
        Assert.Equal((LessonVersionKind.Converted, Notes), (version.Kind, version.TeachersInformation));
        Assert.Equal(["big.jpg", "plan.jpg"], version.Pictures.OrderBy(p => p.Order).Select(p => p.FileName));

        var again = await RunAsync();
        Assert.Equal(0, again.LessonTextsUpdated);
        Assert.Equal(1, await db.LessonVersions.CountAsync(v => v.LessonId == lesson.Id));
    }

    [Fact]
    public async Task NoTeachersInformationFile_MeansNone_AndAddingOrRemovingOneChangesTheContentVersion()
    {
        WriteText("lessons/R1-01.md", Text);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        await RunAsync();
        string withoutNotes;
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            Assert.Null(lesson.TeachersInformation);
            withoutNotes = lesson.ContentVersion!;
        }

        WriteText("lessons/R1-01.teachers-information.md", "Only words.\n");
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            Assert.Equal("Only words.\n", lesson.TeachersInformation);
            Assert.NotEqual(withoutNotes, lesson.ContentVersion);
            var versions = await db.LessonVersions.Where(v => v.LessonId == lesson.Id).OrderBy(v => v.Number).ToListAsync();
            Assert.Equal([(1, (string?)null), (2, "Only words.\n")], versions.Select(v => (v.Number, v.TeachersInformation)));
        }

        Delete("lessons/R1-01.teachers-information.md");
        await RunAsync();
        using var after = CreateContext();
        var cleared = await after.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Null(cleared.TeachersInformation);
        // Without a Teacher's Information file the content version is what it was before it existed.
        Assert.Equal(withoutNotes, cleared.ContentVersion);
    }

    [Theory]
    [InlineData("![Missing](missing.jpg)\n", "not found")]
    [InlineData("![Not a picture](notes.pdf)\n", "is not a JPEG or PNG")]
    [InlineData("![Elsewhere](../R1-02/plan.jpg)\n", "must be a file name")]
    public async Task ABadPictureInTheTeachersInformation_IsAnError_AndNothingIsWritten(string notes, string error)
    {
        WriteText("lessons/R1-01.md", Text);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        WriteText("lessons/R1-01.teachers-information.md", notes);

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("R1-01") && e.Contains("Teacher's Information") && e.Contains(error));
        using var db = CreateContext();
        Assert.False(await db.Lessons.AnyAsync());
        Assert.False(await db.Ranks.AnyAsync());
    }

    [Fact]
    public async Task ATeachersInformationFileWithoutLessonText_IsAWarning_NotImported()
    {
        WriteText("lessons/R1-02.teachers-information.md", "Orphan notes.\n");

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("R1-02.teachers-information.md") && w.Contains("not imported"));
        using var db = CreateContext();
        Assert.Null((await db.Lessons.SingleAsync(l => l.Code == "R1-02")).TeachersInformation);
    }

    [Fact]
    public async Task TeachersInformationChangedThroughASuggestion_IsLeftUntouched_WithAWarning()
    {
        WriteLessonWithInformation();
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            db.Entry(lesson).Property(l => l.TeachersInformation).CurrentValue = "Approved notes.\n";
            db.LessonVersions.Add(new LessonVersion
            {
                Id = await db.NextIdAsync<LessonVersion>(), LessonId = lesson.Id, Number = 2, Kind = LessonVersionKind.ApprovedSuggestion,
                Title = "Intro", Order = 1, IsActive = true, Body = Text, TeachersInformation = "Approved notes.\n", PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        WriteText("lessons/R1-01.teachers-information.md", "Seeder notes.\n");
        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("R1-01") && w.Contains("left untouched"));
        using var after = CreateContext();
        var untouched = await after.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Equal("Approved notes.\n", untouched.TeachersInformation);
        Assert.Equal(2, await after.LessonVersions.CountAsync(v => v.LessonId == untouched.Id));
    }

    [Theory]
    [InlineData("lessons/R1-01.notes.md", "R1-01.teachers-information.md")]
    [InlineData("lessons/R1-TI.md", "no rank-wide Teacher's Information")]
    public async Task ALeftoverNotesOrRankWideFile_IsAValidationError_AndNothingIsWritten(string stale, string message)
    {
        WriteLessonWithInformation();
        WriteText(stale, "Stale.\n");

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains(Path.GetFileName(stale)) && e.Contains(message));
        using var db = CreateContext();
        Assert.False(await db.Lessons.AnyAsync());
    }
}
