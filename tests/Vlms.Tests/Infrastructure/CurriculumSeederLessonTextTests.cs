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
/// <see cref="CurriculumSeeder"/>'s import of app-hosted lessons (docs/adr/0012-app-hosted-lessons.md): lesson text and
/// Teacher's Information from <c>lessons/</c> under the content root, pictures resized and stored, a missing picture a
/// validation error that writes nothing, a re-run with no change changing nothing, and a changed picture changing the
/// content version. Same temporary-folder fixture as <see cref="CurriculumSeederTests"/>.
/// </summary>
public sealed class CurriculumSeederLessonTextTests : IDisposable
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

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly string _tempRoot;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public CurriculumSeederLessonTextTests()
    {
        _connectionString = $"Data Source=file:vlms-seeder-text-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"vlms-seed-text-{Guid.NewGuid():N}");
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

    private void WritePicture(string relativePath, int width, int height, SKColor colour)
    {
        var path = Path.Combine(_tempRoot, "content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(colour);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
    }

    private void WriteConvertedLesson()
    {
        WriteText("lessons/R1-01.md", "## Questions\n\n- Why?\n\n![The big picture](big.jpg)\n\nText.\n\n![The small picture](small.jpg)\n\n![The big picture again](big.jpg)\n");
        WritePicture("lessons/R1-01/big.jpg", 3200, 2000, SKColors.SteelBlue);
        WritePicture("lessons/R1-01/small.jpg", 400, 300, SKColors.Gold);
    }

    [Fact]
    public async Task ConvertedLesson_IsImportedWithItsTextAndResizedPictures_InOrder()
    {
        WriteConvertedLesson();

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(1, result.LessonTextsUpdated);
        Assert.Equal(2, result.PicturesUploaded);
        using var db = CreateContext();
        var lesson = await db.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.StartsWith("## Questions", lesson.Body);
        Assert.Matches("^[0-9a-f]{64}$", lesson.ContentVersion);
        var pictures = await db.LessonPictures.Where(p => p.LessonId == lesson.Id).OrderBy(p => p.Order).ToListAsync();
        Assert.Equal(["big.jpg", "small.jpg"], pictures.Select(p => p.FileName));
        Assert.Equal(["lesson-pictures/R1-01/big.jpg", "lesson-pictures/R1-01/small.jpg"], pictures.Select(p => p.BlobKey));

        Assert.True(_blobStorage.TryGetBlob("lesson-pictures/R1-01/big.jpg", out var big));
        using var codec = SKCodec.Create(new MemoryStream(big!));
        Assert.Equal((1600, 1000), (codec.Info.Width, codec.Info.Height));

        // A lesson with no file stays unconverted.
        var second = await db.Lessons.SingleAsync(l => l.Code == "R1-02");
        Assert.Null(second.Body);
        Assert.Null(second.ContentVersion);
    }

    [Fact]
    public async Task SecondRun_WithNoChange_ChangesNothing()
    {
        WriteConvertedLesson();
        await RunAsync();
        using var before = CreateContext();
        var lessonBefore = await before.Lessons.AsNoTracking().SingleAsync(l => l.Code == "R1-01");
        var picturesBefore = await before.LessonPictures.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.FileName, p.Order }).ToListAsync();

        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal((0, 0, 0, 0), (result.LessonTextsUpdated, result.PicturesUploaded, result.LessonsCreated, result.LessonsUpdated));
        using var after = CreateContext();
        var lessonAfter = await after.Lessons.AsNoTracking().SingleAsync(l => l.Code == "R1-01");
        Assert.Equal((lessonBefore.Body, lessonBefore.ContentVersion), (lessonAfter.Body, lessonAfter.ContentVersion));
        Assert.Equal(picturesBefore, await after.LessonPictures.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.FileName, p.Order }).ToListAsync());
    }

    [Fact]
    public async Task ChangedPicture_ChangesTheContentVersion_AndUploadsAgain_KeepingPictureIds()
    {
        WriteConvertedLesson();
        await RunAsync();
        using var before = CreateContext();
        var versionBefore = (await before.Lessons.SingleAsync(l => l.Code == "R1-01")).ContentVersion;
        var idsBefore = await before.LessonPictures.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();

        WritePicture("lessons/R1-01/small.jpg", 400, 300, SKColors.Crimson);
        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.LessonTextsUpdated);
        Assert.Equal(2, result.PicturesUploaded);
        using var after = CreateContext();
        Assert.NotEqual(versionBefore, (await after.Lessons.SingleAsync(l => l.Code == "R1-01")).ContentVersion);
        Assert.Equal(idsBefore, await after.LessonPictures.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task ChangedText_ChangesTheContentVersion()
    {
        WriteConvertedLesson();
        await RunAsync();
        using var before = CreateContext();
        var versionBefore = (await before.Lessons.SingleAsync(l => l.Code == "R1-01")).ContentVersion;

        WriteText("lessons/R1-01.md", "## Questions\n\nChanged.\n\n![The big picture](big.jpg)\n");
        var result = await RunAsync();

        Assert.True(result.Succeeded);
        using var after = CreateContext();
        var lesson = await after.Lessons.SingleAsync(l => l.Code == "R1-01");
        Assert.NotEqual(versionBefore, lesson.ContentVersion);
        Assert.Contains("Changed.", lesson.Body);
        Assert.Equal(["big.jpg"], await after.LessonPictures.Where(p => p.LessonId == lesson.Id).Select(p => p.FileName).ToListAsync());
    }

    [Fact]
    public async Task MissingPicture_IsAValidationError_AndNothingIsWritten()
    {
        WriteText("lessons/R1-01.md", "## Text\n\n![Gone](missing.jpg)\n\n![Elsewhere](../R1-02/a.jpg)\n");

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("R1-01") && e.Contains("missing.jpg") && e.Contains("not found"));
        Assert.Contains(result.Errors, e => e.Contains("../R1-02/a.jpg"));
        using var db = CreateContext();
        Assert.Empty(await db.Lessons.ToListAsync());
        Assert.Empty(await db.LessonPictures.ToListAsync());
        Assert.False(_blobStorage.TryGetBlob("lessons/R1-01/Main.docx", out _));
    }

    [Fact]
    public async Task UnreadablePicture_IsAValidationError()
    {
        WriteText("lessons/R1-01.md", "![Broken](broken.jpg)\n");
        WriteText("lessons/R1-01/broken.jpg", "not a picture");

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("broken.jpg") && e.Contains("could not be read"));
    }

    [Fact]
    public async Task TeachersInformation_IsImportedForItsRank_WithItsPictures()
    {
        WriteText("lessons/R1-TI.md", "## For teachers\n\nPrepare the room.\n\n![Room layout](room.jpg)\n");
        WritePicture("lessons/R1-TI/room.jpg", 640, 480, SKColors.Olive);

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        using var db = CreateContext();
        var rank = await db.Ranks.SingleAsync(r => r.Code == "RECRUIT");
        Assert.StartsWith("## For teachers", rank.TeachersInformation);
        var picture = await db.TeachersInformationPictures.SingleAsync();
        Assert.Equal((rank.Id, "room.jpg", "teachers-information/RECRUIT/room.jpg"), (picture.RankId, picture.FileName, picture.BlobKey));
        Assert.True(_blobStorage.TryGetBlob(picture.BlobKey, out _));

        var again = await RunAsync();
        Assert.Equal(0, again.LessonTextsUpdated);
    }

    [Fact]
    public async Task TeachersInformation_ByRankCode_IsAlsoRecognised_ButNotTwice()
    {
        WriteText("lessons/RECRUIT-TI.md", "Notes.\n");
        Assert.True((await RunAsync()).Succeeded);

        WriteText("lessons/R1-TI.md", "Other notes.\n");
        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("two Teacher's Information files"));
    }

    [Fact]
    public async Task MarkdownForNoLesson_AndUnusedPicture_AreWarnings()
    {
        WriteConvertedLesson();
        WriteText("lessons/R9-99.md", "Nobody's lesson.\n");
        WritePicture("lessons/R1-01/unused.jpg", 10, 10, SKColors.Black);

        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("R9-99.md"));
        Assert.Contains(result.Warnings, w => w.Contains("unused.jpg"));
    }

    [Fact]
    public async Task RealHeraldLessons_ValidateAgainstTheRealManifest()
    {
        // The committed seed/lessons files: every picture they refer to exists and reads (the content root's lesson
        // folders are not in git, so only the lesson-text errors are looked at).
        var repo = FindRepoRoot();
        var lessonsCopy = Path.Combine(_tempRoot, "content", "lessons");
        CopyDirectory(Path.Combine(repo, "seed", "lessons"), lessonsCopy);
        using var db = CreateContext();

        var result = await new CurriculumSeeder(db, _blobStorage).SeedAsync(Path.Combine(repo, "seed", "curriculum.json"), Path.Combine(_tempRoot, "content"));

        Assert.DoesNotContain(result.Errors, e => e.Contains("picture") || e.Contains("Teacher's Information"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed", "curriculum.json")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
