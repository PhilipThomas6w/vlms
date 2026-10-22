using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="CurriculumSeeder"/> (STATE.md "Seed rank ladder + curriculum"): idempotent
/// upsert of ranks, lessons, tags and files from a manifest, validate-first-write-nothing, and file
/// ordering. Uses a small temporary folder fixture built per test, never the real (untracked) seed
/// content; one test parses the real <c>seed/curriculum.json</c> for structure only.
/// </summary>
public sealed class CurriculumSeederTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly string _tempRoot;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public CurriculumSeederTests()
    {
        _connectionString = $"Data Source=file:vlms-seeder-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"vlms-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
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

    private async Task<CurriculumSeedResult> RunAsync(string manifestJson)
    {
        var manifestPath = Path.Combine(_tempRoot, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);
        using var db = CreateContext();
        return await new CurriculumSeeder(db, _blobStorage).SeedAsync(manifestPath, Path.Combine(_tempRoot, "content"));
    }

    private void WriteContent(string relativePath, string text = "x")
    {
        var path = Path.Combine(_tempRoot, "content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Manifest(string lessonTitle = "Intro", string tag = "Church Life", string lessonSource = "1. Intro") => $$"""
        {
          "ranks": [
            { "order": 1, "code": "RECRUIT", "name": "Recruit", "source": "Recruits (1)", "lessons": [
              { "order": 1, "code": "R1-01", "title": "{{lessonTitle}}", "tags": ["{{tag}}", "Worship"], "source": "{{lessonSource}}" } ] },
            { "order": 2, "code": "GUARDSMAN", "name": "Guardsman", "source": "Guardsman (2)", "lessons": [] }
          ]
        }
        """;

    private void WriteStandardContent()
    {
        WriteContent("Recruits (1)/1. Intro/B Handout.docx");
        WriteContent("Recruits (1)/1. Intro/A Main.docx");
        WriteContent("Recruits (1)/1. Intro/Images/z.jpg");
        WriteContent("Recruits (1)/1. Intro/Images/a.jpg");
    }

    private static async Task<(List<Rank> Ranks, List<Lesson> Lessons, List<LessonTagAssignment> Tags, List<LessonFile> Files)> SnapshotAsync(VlmsDbContext db) =>
        (await db.Ranks.OrderBy(r => r.Id).ToListAsync(), await db.Lessons.OrderBy(l => l.Id).ToListAsync(),
         await db.LessonTagAssignments.OrderBy(t => t.LessonId).ThenBy(t => t.Tag).ToListAsync(),
         await db.LessonFiles.OrderBy(f => f.Id).ToListAsync());

    [Fact]
    public async Task FirstRun_CreatesRanksLessonsTagsAndFiles()
    {
        WriteStandardContent();

        var result = await RunAsync(Manifest());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RanksCreated);
        Assert.Equal(1, result.LessonsCreated);
        Assert.Equal(4, result.FilesUploaded);
        using var db = CreateContext();
        var (ranks, lessons, tags, files) = await SnapshotAsync(db);
        Assert.Equal(["RECRUIT", "GUARDSMAN"], ranks.Select(r => r.Code));
        var lesson = Assert.Single(lessons);
        Assert.Equal((ranks[0].Id, "Intro", 1, true), (lesson.RankId, lesson.Title, lesson.Order, lesson.IsActive));
        Assert.Equal([LessonTag.ChurchLife, LessonTag.Worship], tags.Select(t => t.Tag));
        Assert.Equal(4, files.Count);
        Assert.True(_blobStorage.TryGetBlob("lessons/R1-01/A Main.docx", out _));
    }

    [Fact]
    public async Task SecondRun_ChangesNothing()
    {
        WriteStandardContent();
        await RunAsync(Manifest());
        using var before = CreateContext();
        var first = await SnapshotAsync(before);

        var result = await RunAsync(Manifest());

        Assert.True(result.Succeeded);
        Assert.Equal((0, 0, 0, 0), (result.RanksCreated, result.RanksUpdated, result.LessonsCreated, result.LessonsUpdated));
        using var after = CreateContext();
        var second = await SnapshotAsync(after);
        Assert.Equal(first.Ranks.Select(r => (r.Id, r.Code, r.Order, r.Name)), second.Ranks.Select(r => (r.Id, r.Code, r.Order, r.Name)));
        Assert.Equal(first.Lessons.Select(l => (l.Id, l.Code, l.Title)), second.Lessons.Select(l => (l.Id, l.Code, l.Title)));
        Assert.Equal(first.Files.Select(f => (f.Id, f.Order, f.FileName, f.BlobKey)), second.Files.Select(f => (f.Id, f.Order, f.FileName, f.BlobKey)));
        Assert.Equal(first.Tags.Count, second.Tags.Count);
    }

    [Fact]
    public async Task ChangedTitleTagAndFile_AreApplied()
    {
        WriteStandardContent();
        await RunAsync(Manifest());
        using var before = CreateContext();
        var lessonId = (await before.Lessons.SingleAsync()).Id;

        WriteContent("Recruits (1)/1. Intro/C New.docx");
        var result = await RunAsync(Manifest(lessonTitle: "Renamed", tag: "Saints"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.LessonsUpdated);
        using var db = CreateContext();
        var (_, lessons, tags, files) = await SnapshotAsync(db);
        var lesson = Assert.Single(lessons);
        Assert.Equal((lessonId, "Renamed"), (lesson.Id, lesson.Title));
        Assert.Equal(new HashSet<LessonTag> { LessonTag.Worship, LessonTag.Saints }, tags.Select(t => t.Tag).ToHashSet());
        Assert.Equal(5, files.Count);
        Assert.Contains(files, f => f.FileName == "C New.docx");
    }

    [Fact]
    public async Task MissingFolderAndUnknownTag_FailValidation_AndWriteNothing()
    {
        // The content root exists but the lesson's folder deliberately does not.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "content"));
        var result = await RunAsync(Manifest(tag: "Not A Tag"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("unknown tag 'Not A Tag'"));
        Assert.Contains(result.Errors, e => e.Contains("folder not found"));
        using var db = CreateContext();
        Assert.Empty(db.Ranks);
        Assert.Empty(db.Lessons);
        Assert.Empty(db.LessonFiles);
        Assert.False(_blobStorage.TryGetBlob("lessons/R1-01/A Main.docx", out _));
    }

    [Fact]
    public async Task DuplicateCodes_FailValidation()
    {
        WriteStandardContent();
        var manifest = """
            { "ranks": [
              { "order": 1, "code": "A", "name": "A", "source": "Recruits (1)", "lessons": [
                { "order": 1, "code": "L", "title": "One", "tags": [], "source": "1. Intro" },
                { "order": 2, "code": "L", "title": "Two", "tags": [], "source": "1. Intro" } ] } ] }
            """;

        var result = await RunAsync(manifest);

        Assert.Contains(result.Errors, e => e.Contains("duplicate lesson code"));
    }

    [Fact]
    public async Task RanksWithNoLessons_AreCreated()
    {
        WriteStandardContent();

        await RunAsync(Manifest());

        using var db = CreateContext();
        Assert.Contains(await db.Ranks.ToListAsync(), r => r.Code == "GUARDSMAN" && r.Name == "Guardsman" && r.Order == 2);
    }

    [Fact]
    public async Task JunkFiles_AreSkipped_AndOrderIsDocumentsThenImages()
    {
        WriteStandardContent();
        WriteContent("Recruits (1)/1. Intro/~$A Main.docx");
        WriteContent("Recruits (1)/1. Intro/scratch.tmp");
        WriteContent("Recruits (1)/1. Intro/Thumbs.db");
        WriteContent("Recruits (1)/1. Intro/desktop.ini");
        WriteContent("Recruits (1)/1. Intro/Images/Thumbs.db");

        await RunAsync(Manifest());

        using var db = CreateContext();
        var files = await db.LessonFiles.OrderBy(f => f.Order).ToListAsync();
        Assert.Equal(["A Main.docx", "B Handout.docx", "a.jpg", "z.jpg"], files.Select(f => f.FileName));
        Assert.Equal([1, 2, 3, 4], files.Select(f => f.Order));
        Assert.Equal("lessons/R1-01/Images/a.jpg", files[2].BlobKey);
    }

    [Fact]
    public async Task LessonRemovedFromManifest_IsKept_AndReportedAsWarning()
    {
        WriteStandardContent();
        await RunAsync(Manifest());

        var result = await RunAsync("""{ "ranks": [ { "order": 1, "code": "RECRUIT", "name": "Recruit", "source": "Recruits (1)", "lessons": [] } ] }""");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("R1-01"));
        Assert.Contains(result.Warnings, w => w.Contains("GUARDSMAN"));
        using var db = CreateContext();
        Assert.Single(db.Lessons);
    }

    [Fact]
    public void RealManifest_HasExpectedStructure()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "seed", "curriculum.json")));
        var ranks = json.RootElement.GetProperty("ranks").EnumerateArray().ToList();
        var lessons = ranks.SelectMany(r => r.GetProperty("lessons").EnumerateArray()).ToList();
        var validTags = Enum.GetNames<LessonTag>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(10, ranks.Count);
        Assert.Equal(56, lessons.Count);
        Assert.Equal(ranks.Count, ranks.Select(r => r.GetProperty("code").GetString()).Distinct().Count());
        Assert.Equal(lessons.Count, lessons.Select(l => l.GetProperty("code").GetString()).Distinct().Count());
        Assert.All(lessons.SelectMany(l => l.GetProperty("tags").EnumerateArray()),
            t => Assert.Contains(new string(t.GetString()!.Where(char.IsLetter).ToArray()), validTags));
    }
}
