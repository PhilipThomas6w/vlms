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
/// upsert of ranks, lessons and tags from a manifest, and validate-first-write-nothing. Handouts are covered by
/// <see cref="CurriculumSeederHandoutTests"/> (docs/adr/0016). Uses a small temporary folder fixture built per test; one
/// test parses the real <c>seed/curriculum.json</c> for structure only.
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


    private static string Manifest(string lessonTitle = "Intro", string tag = "Church Life") => $$"""
        {
          "ranks": [
            { "order": 1, "code": "RECRUIT", "name": "Recruit", "lessons": [
              { "order": 1, "code": "R1-01", "title": "{{lessonTitle}}", "tags": ["{{tag}}", "Worship"] } ] },
            { "order": 2, "code": "GUARDSMAN", "name": "Guardsman", "lessons": [] }
          ]
        }
        """;

    // The content root (the original rank folders are no longer read, docs/adr/0016).
    private void WriteStandardContent() => Directory.CreateDirectory(Path.Combine(_tempRoot, "content", "lessons"));

    private static async Task<(List<Rank> Ranks, List<Lesson> Lessons, List<LessonTagAssignment> Tags, List<LessonFile> Files)> SnapshotAsync(VlmsDbContext db) =>
        (await db.Ranks.OrderBy(r => r.Id).ToListAsync(), await db.Lessons.OrderBy(l => l.Id).ToListAsync(),
         await db.LessonTagAssignments.OrderBy(t => t.LessonId).ThenBy(t => t.Tag).ToListAsync(),
         await db.LessonFiles.OrderBy(f => f.Id).ToListAsync());

    [Fact]
    public async Task FirstRun_CreatesRanksLessonsAndTags()
    {
        WriteStandardContent();

        var result = await RunAsync(Manifest());

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(2, result.RanksCreated);
        Assert.Equal(1, result.LessonsCreated);
        using var db = CreateContext();
        var (ranks, lessons, tags, files) = await SnapshotAsync(db);
        Assert.Equal(["RECRUIT", "GUARDSMAN"], ranks.Select(r => r.Code));
        var lesson = Assert.Single(lessons);
        Assert.Equal((ranks[0].Id, "Intro", 1, true), (lesson.RankId, lesson.Title, lesson.Order, lesson.IsActive));
        Assert.Equal([LessonTag.ChurchLife, LessonTag.Worship], tags.Select(t => t.Tag));
        Assert.Empty(files);
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
    public async Task ChangedTitleAndTag_AreApplied()
    {
        WriteStandardContent();
        await RunAsync(Manifest());
        using var before = CreateContext();
        var lessonId = (await before.Lessons.SingleAsync()).Id;

        var result = await RunAsync(Manifest(lessonTitle: "Renamed", tag: "Saints"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.LessonsUpdated);
        using var db = CreateContext();
        var (_, lessons, tags, _) = await SnapshotAsync(db);
        var lesson = Assert.Single(lessons);
        Assert.Equal((lessonId, "Renamed"), (lesson.Id, lesson.Title));
        Assert.Equal(new HashSet<LessonTag> { LessonTag.Worship, LessonTag.Saints }, tags.Select(t => t.Tag).ToHashSet());
    }

    [Fact]
    public async Task UnknownTag_FailsValidation_AndWritesNothing()
    {
        WriteStandardContent();
        var result = await RunAsync(Manifest(tag: "Not A Tag"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("unknown tag 'Not A Tag'"));
        using var db = CreateContext();
        Assert.Empty(db.Ranks);
        Assert.Empty(db.Lessons);
    }

    [Fact]
    public async Task MissingContentRoot_FailsValidation()
    {
        var result = await RunAsync(Manifest());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Content root not found"));
    }

    [Fact]
    public async Task DuplicateCodes_FailValidation()
    {
        WriteStandardContent();
        var manifest = """
            { "ranks": [
              { "order": 1, "code": "A", "name": "A", "lessons": [
                { "order": 1, "code": "L", "title": "One", "tags": [] },
                { "order": 2, "code": "L", "title": "Two", "tags": [] } ] } ] }
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
    public async Task LessonRemovedFromManifest_IsKept_AndReportedAsWarning()
    {
        WriteStandardContent();
        await RunAsync(Manifest());

        var result = await RunAsync("""{ "ranks": [ { "order": 1, "code": "RECRUIT", "name": "Recruit", "lessons": [] } ] }""");

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
        Assert.Equal(64, lessons.Count);
        Assert.Equal(ranks.Count, ranks.Select(r => r.GetProperty("code").GetString()).Distinct().Count());
        Assert.Equal(lessons.Count, lessons.Select(l => l.GetProperty("code").GetString()).Distinct().Count());
        Assert.All(lessons.SelectMany(l => l.GetProperty("tags").EnumerateArray()),
            t => Assert.Contains(new string(t.GetString()!.Where(char.IsLetter).ToArray()), validTags));
    }
}
