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
/// <see cref="CurriculumSeeder"/> and docs/adr/0015 decision 5: a lesson's teacher's notes from
/// <c>lessons/{code}.notes.md</c>, pictures in the lesson's folder under the same rules (bare names, JPEG/PNG, missing is an
/// error and nothing is written), part of the content version; no file means no notes; and the "only while the latest
/// version came from the seeder" rule (adr/0013 decision 9) for the notes and for a rank's Teacher's Information, which
/// now has versions. Same temporary-folder fixture as <see cref="CurriculumSeederLessonTextTests"/>.
/// </summary>
public sealed class CurriculumSeederTeachersNotesTests : IDisposable
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

    public CurriculumSeederTeachersNotesTests()
    {
        _connectionString = $"Data Source=file:vlms-seeder-notes-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"vlms-seed-notes-{Guid.NewGuid():N}");
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

    private void WriteLessonWithNotes()
    {
        WriteText("lessons/R1-01.md", Text);
        WriteText("lessons/R1-01.notes.md", Notes);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        WritePicture("lessons/R1-01/plan.jpg", SKColors.Gold);
    }

    [Fact]
    public async Task Notes_AreImportedWithTheLesson_APictureOnlyTheyUseMarkedStaffOnly()
    {
        WriteLessonWithNotes();

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("plan.jpg") || w.Contains("R1-01.notes"));
        using var db = CreateContext();
        var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Equal((Text, Notes), (lesson.Body, lesson.TeachersNotes));
        var pictures = await db.LessonPictures.Where(p => p.LessonId == lesson.Id).OrderBy(p => p.Order).ToListAsync();
        Assert.Equal([("big.jpg", false), ("plan.jpg", true)], pictures.Select(p => (p.FileName, p.TeachersNotesOnly)));
        Assert.All(pictures, p => Assert.True(_blobStorage.TryGetBlob(p.BlobKey, out _)));
        var version = await db.LessonVersions.Include(v => v.Pictures).SingleAsync(v => v.LessonId == lesson.Id);
        Assert.Equal((LessonVersionKind.Converted, Notes), (version.Kind, version.TeachersNotes));
        Assert.Equal(["big.jpg", "plan.jpg"], version.Pictures.OrderBy(p => p.Order).Select(p => p.FileName));

        var again = await RunAsync();
        Assert.Equal(0, again.LessonTextsUpdated);
        Assert.Equal(1, await db.LessonVersions.CountAsync(v => v.LessonId == lesson.Id));
    }

    [Fact]
    public async Task NoNotesFile_MeansNoNotes_AndAddingOrRemovingOneChangesTheContentVersion()
    {
        WriteText("lessons/R1-01.md", Text);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        await RunAsync();
        string withoutNotes;
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            Assert.Null(lesson.TeachersNotes);
            withoutNotes = lesson.ContentVersion!;
        }

        WriteText("lessons/R1-01.notes.md", "Only words.\n");
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            Assert.Equal("Only words.\n", lesson.TeachersNotes);
            Assert.NotEqual(withoutNotes, lesson.ContentVersion);
            var versions = await db.LessonVersions.Where(v => v.LessonId == lesson.Id).OrderBy(v => v.Number).ToListAsync();
            Assert.Equal([(1, (string?)null), (2, "Only words.\n")], versions.Select(v => (v.Number, v.TeachersNotes)));
        }

        Delete("lessons/R1-01.notes.md");
        await RunAsync();
        using var after = CreateContext();
        var cleared = await after.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Null(cleared.TeachersNotes);
        // Without a notes file the content version is what it was before notes existed.
        Assert.Equal(withoutNotes, cleared.ContentVersion);
    }

    [Theory]
    [InlineData("![Missing](missing.jpg)\n", "not found")]
    [InlineData("![Not a picture](notes.pdf)\n", "is not a JPEG or PNG")]
    [InlineData("![Elsewhere](../R1-02/plan.jpg)\n", "must be a file name")]
    public async Task ABadPictureInTheNotes_IsAnError_AndNothingIsWritten(string notes, string error)
    {
        WriteText("lessons/R1-01.md", Text);
        WritePicture("lessons/R1-01/big.jpg", SKColors.SteelBlue);
        WriteText("lessons/R1-01.notes.md", notes);

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("R1-01") && e.Contains("teacher's notes") && e.Contains(error));
        using var db = CreateContext();
        Assert.False(await db.Lessons.AnyAsync());
        Assert.False(await db.Ranks.AnyAsync());
    }

    [Fact]
    public async Task ANotesFileWithoutLessonText_IsAWarning_NotImported()
    {
        WriteText("lessons/R1-02.notes.md", "Orphan notes.\n");

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("R1-02.notes.md") && w.Contains("not imported"));
        using var db = CreateContext();
        Assert.Null((await db.Lessons.SingleAsync(l => l.Code == "R1-02")).TeachersNotes);
    }

    [Fact]
    public async Task NotesChangedThroughASuggestion_AreLeftUntouched_WithAWarning()
    {
        WriteLessonWithNotes();
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
            db.Entry(lesson).Property(l => l.TeachersNotes).CurrentValue = "Approved notes.\n";
            db.LessonVersions.Add(new LessonVersion
            {
                Id = await db.NextIdAsync<LessonVersion>(), LessonId = lesson.Id, Number = 2, Kind = LessonVersionKind.ApprovedSuggestion,
                Title = "Intro", Order = 1, IsActive = true, Body = Text, TeachersNotes = "Approved notes.\n", PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        WriteText("lessons/R1-01.notes.md", "Seeder notes.\n");
        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("R1-01") && w.Contains("left untouched"));
        using var after = CreateContext();
        var untouched = await after.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.Equal("Approved notes.\n", untouched.TeachersNotes);
        Assert.Equal(2, await after.LessonVersions.CountAsync(v => v.LessonId == untouched.Id));
    }

    // --- Rank Teacher's Information versions ------------------------------------------------------------------------

    [Fact]
    public async Task TeachersInformation_IsRecordedAsConvertedVersions_OnlyWhenItChanges()
    {
        WriteText("lessons/R1-TI.md", "## For teachers\n\nFirst.\n\n![Room](room.jpg)\n");
        WritePicture("lessons/R1-TI/room.jpg", SKColors.Olive);
        await RunAsync();
        await RunAsync();

        using (var db = CreateContext())
        {
            var version = await db.TeachersInformationVersions.Include(v => v.Pictures).SingleAsync();
            Assert.Equal((1, LessonVersionKind.Converted), (version.Number, version.Kind));
            Assert.Contains("First.", version.Body);
            var live = await db.TeachersInformationPictures.SingleAsync();
            Assert.Equal(live.BlobKey, Assert.Single(version.Pictures).BlobKey);
        }

        WriteText("lessons/R1-TI.md", "## For teachers\n\nSecond.\n\n![Room](room.jpg)\n");
        WritePicture("lessons/R1-TI/room.jpg", SKColors.Maroon);
        await RunAsync();

        using var after = CreateContext();
        var versions = await after.TeachersInformationVersions.Include(v => v.Pictures).OrderBy(v => v.Number).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        Assert.All(versions, v => Assert.Equal(LessonVersionKind.Converted, v.Kind));
        // A changed picture has a new key, so version 1 still shows the one it had.
        Assert.NotEqual(versions[0].Pictures.Single().BlobKey, versions[1].Pictures.Single().BlobKey);
        Assert.True(_blobStorage.TryGetBlob(versions[0].Pictures.Single().BlobKey, out _));
    }

    [Fact]
    public async Task TeachersInformationChangedThroughASuggestion_IsLeftUntouched_WithAWarning()
    {
        WriteText("lessons/R1-TI.md", "## For teachers\n\nSeeded.\n");
        await RunAsync();
        using (var db = CreateContext())
        {
            var rank = await db.Ranks.SingleAsync(r => r.Code == "RECRUIT");
            db.Entry(rank).Property(r => r.TeachersInformation).CurrentValue = "Approved guidance.\n";
            db.TeachersInformationVersions.Add(new TeachersInformationVersion
            {
                Id = await db.NextIdAsync<TeachersInformationVersion>(), RankId = rank.Id, Number = 2, Kind = LessonVersionKind.ApprovedSuggestion,
                Body = "Approved guidance.\n", PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        WriteText("lessons/R1-TI.md", "## For teachers\n\nSeeder again.\n\n![Room](room.jpg)\n");
        WritePicture("lessons/R1-TI/room.jpg", SKColors.Olive);
        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("RECRUIT") && w.Contains("Teacher's Information") && w.Contains("left untouched"));
        Assert.Equal(0, result.PicturesUploaded);
        using var after = CreateContext();
        Assert.Equal("Approved guidance.\n", (await after.Ranks.SingleAsync(r => r.Code == "RECRUIT")).TeachersInformation);
        Assert.Equal(2, await after.TeachersInformationVersions.CountAsync());
        Assert.False(await after.TeachersInformationPictures.AnyAsync());
    }
}
