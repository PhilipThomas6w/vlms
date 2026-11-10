using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// <see cref="CurriculumSeeder"/>'s handouts (docs/adr/0016): read from <c>lessons/{code}/handouts/</c>, checked by type and
/// size, stored content-addressed, recorded in the lesson's version; ids kept for handouts that remain; a lesson's other
/// current files pruned (rows, and stored files nothing else refers to) and counted; a lesson changed through a suggestion
/// left untouched. Same temporary-folder fixture as <see cref="CurriculumSeederTests"/>.
/// </summary>
public sealed class CurriculumSeederHandoutTests : IDisposable
{
    private const string Manifest = """
        { "ranks": [ { "order": 1, "code": "RECRUIT", "name": "Recruit", "lessons": [
          { "order": 1, "code": "R1-01", "title": "Intro", "tags": ["Church Life"] } ] } ] }
        """;

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly string _tempRoot;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public CurriculumSeederHandoutTests()
    {
        _connectionString = $"Data Source=file:vlms-seeder-handout-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();
        using var schema = CreateContext();
        schema.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"vlms-seed-handouts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "content", "lessons"));
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

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_tempRoot, "content", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void Delete(string relativePath) => File.Delete(Path.Combine(_tempRoot, "content", relativePath));

    private async Task<List<LessonFile>> FilesAsync()
    {
        using var db = CreateContext();
        return await db.LessonFiles.AsNoTracking().OrderBy(f => f.Order).ToListAsync();
    }

    [Fact]
    public async Task Handouts_AreImported_ContentAddressed_WithDisplayNames_AndRecordedInTheVersion()
    {
        Write("lessons/R1-01/handouts/Fasting dial template.pdf", HandoutBytes.Pdf());
        Write("lessons/R1-01/handouts/Colouring page.png", HandoutBytes.Png());

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(2, result.HandoutsUploaded);
        var files = await FilesAsync();
        Assert.Equal(["Colouring page", "Fasting dial template"], files.Select(f => f.DisplayName));
        Assert.Equal(["Colouring page.png", "Fasting dial template.pdf"], files.Select(f => f.FileName));
        Assert.All(files, f => Assert.Matches("^lesson-handouts/R1-01/[0-9a-f]{16}\\.(pdf|png)$", f.BlobKey));
        Assert.All(files, f => Assert.True(_blobStorage.TryGetBlob(f.BlobKey, out _)));
        using var db = CreateContext();
        var version = await db.LessonVersions.Include(v => v.Handouts).SingleAsync();
        Assert.Equal(files.Select(f => (f.DisplayName, f.BlobKey)), version.Handouts.OrderBy(h => h.Order).Select(h => (h.DisplayName, h.BlobKey)));
    }

    [Fact]
    public async Task SecondRun_WithNoChange_ChangesNothing()
    {
        Write("lessons/R1-01/handouts/Template.pdf", HandoutBytes.Pdf());
        await RunAsync();
        var before = await FilesAsync();

        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal((0, 0, 0, 0), (result.HandoutsUploaded, result.FilesRemoved, result.LessonsUpdated, result.BlobsDeleted));
        Assert.Equal(before.Select(f => (f.Id, f.BlobKey, f.DisplayName)), (await FilesAsync()).Select(f => (f.Id, f.BlobKey, f.DisplayName)));
        using var db = CreateContext();
        Assert.Equal(1, await db.LessonVersions.CountAsync());
    }

    [Theory]
    [InlineData("Template.pdf", "text")]
    [InlineData("Template.docx", "zip")]
    [InlineData("Template.png", "pdf")]
    [InlineData("script.js", "pdf")]
    public async Task AFileThatIsNotWhatItsNameSays_OrNotAnAllowedType_IsAnError_AndNothingIsWritten(string name, string content)
    {
        Write("lessons/R1-01/handouts/" + name, content switch
        {
            "zip" => HandoutBytes.Zip(),
            "pdf" => HandoutBytes.Pdf(),
            _ => Encoding.UTF8.GetBytes("<script>alert(1)</script>")
        });

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains(name) && e.Contains("not a PDF"));
        using var db = CreateContext();
        Assert.Empty(await db.Lessons.ToListAsync());
    }

    [Fact]
    public async Task AWordDocument_IsAccepted()
    {
        Write("lessons/R1-01/handouts/Worksheet.docx", HandoutBytes.Docx());

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("Worksheet.docx", Assert.Single(await FilesAsync()).FileName);
    }

    [Fact]
    public async Task AHandoutOverTheSizeLimit_IsAnError()
    {
        var big = new byte[LessonHandoutFiles.MaxBytes + 1];
        HandoutBytes.Pdf().CopyTo(big, 0);
        Write("lessons/R1-01/handouts/Big.pdf", big);

        var result = await RunAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Big.pdf") && e.Contains("larger than 20 MB"));
    }

    [Fact]
    public async Task FilesNotInHandouts_ArePruned_KeepingTheIdOfOneThatRemains_AndTheRunSaysHowMany()
    {
        // The lesson as the old seeder left it: two files from the original rank folder.
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync();
            await _blobStorage.UploadAsync("lessons/R1-01/Main.docx", HandoutBytes.Docx("duplicate"));
            await _blobStorage.UploadAsync("lessons/R1-01/Images/template.pdf", HandoutBytes.Pdf("template"));
            db.LessonFiles.Add(new LessonFile { Id = 7, LessonId = lesson.Id, Order = 1, FileName = "Main.docx", DisplayName = "Main", BlobKey = "lessons/R1-01/Main.docx" });
            db.LessonFiles.Add(new LessonFile { Id = 8, LessonId = lesson.Id, Order = 2, FileName = "template.pdf", DisplayName = "template", BlobKey = "lessons/R1-01/Images/template.pdf" });
            await db.SaveChangesAsync();
        }
        // The audit keeps the template (same content, a better name) and drops the duplicate.
        Write("lessons/R1-01/handouts/Fasting dial template.pdf", HandoutBytes.Pdf("template"));

        var result = await RunAsync();

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(1, result.FilesRemoved);
        Assert.Equal(2, result.BlobsDeleted); // the duplicate, and the template's old non-content-addressed copy
        var kept = Assert.Single(await FilesAsync());
        Assert.Equal((8, "Fasting dial template", 1), (kept.Id, kept.DisplayName, kept.Order));
        Assert.StartsWith("lesson-handouts/R1-01/", kept.BlobKey);
        Assert.False(_blobStorage.TryGetBlob("lessons/R1-01/Main.docx", out _));
        Assert.False(_blobStorage.TryGetBlob("lessons/R1-01/Images/template.pdf", out _));
        Assert.True(_blobStorage.TryGetBlob(kept.BlobKey, out _));
    }

    [Fact]
    public async Task AHandoutRemovedFromTheFolder_IsPruned_ButItsStoredFileStays_WhileAnEarlierVersionRefersToIt()
    {
        Write("lessons/R1-01/handouts/One.pdf", HandoutBytes.Pdf("one"));
        Write("lessons/R1-01/handouts/Two.pdf", HandoutBytes.Pdf("two"));
        await RunAsync();
        var two = (await FilesAsync()).Single(f => f.DisplayName == "Two");

        Delete("lessons/R1-01/handouts/Two.pdf");
        var result = await RunAsync();

        Assert.Equal((1, 0), (result.FilesRemoved, result.BlobsDeleted));
        Assert.Equal(["One"], (await FilesAsync()).Select(f => f.DisplayName));
        // Version 1 still lists it, so it stays downloadable from the history.
        Assert.True(_blobStorage.TryGetBlob(two.BlobKey, out _));
        using var db = CreateContext();
        var versions = await db.LessonVersions.Include(v => v.Handouts).OrderBy(v => v.Number).ToListAsync();
        Assert.Equal([2, 1], versions.Select(v => v.Handouts.Count));
    }

    [Fact]
    public async Task ALessonChangedThroughASuggestion_KeepsItsHandouts_WithAWarning()
    {
        Write("lessons/R1-01/handouts/One.pdf", HandoutBytes.Pdf("one"));
        await RunAsync();
        using (var db = CreateContext())
        {
            var lesson = await db.Lessons.SingleAsync();
            db.LessonVersions.Add(new LessonVersion
            {
                Id = await db.NextIdAsync<LessonVersion>(), LessonId = lesson.Id, Number = 2, Kind = LessonVersionKind.ApprovedSuggestion,
                Title = "Intro", Order = 1, IsActive = true, PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var before = await FilesAsync();

        Delete("lessons/R1-01/handouts/One.pdf");
        Write("lessons/R1-01/handouts/Other.pdf", HandoutBytes.Pdf("other"));
        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("R1-01") && w.Contains("left untouched"));
        Assert.Equal((0, 0, 0), (result.FilesRemoved, result.HandoutsUploaded, result.BlobsDeleted));
        Assert.Equal(before.Select(f => (f.Id, f.BlobKey)), (await FilesAsync()).Select(f => (f.Id, f.BlobKey)));
    }

    [Fact]
    public async Task AConvertedLessonsContentVersion_ChangesWithItsHandouts_SoSavedCopiesRefresh()
    {
        Write("lessons/R1-01.md", Encoding.UTF8.GetBytes("## Text\n"));
        await RunAsync();
        string? Version() { using var db = CreateContext(); return db.Lessons.Single().ContentVersion; }
        var withNone = Version();

        Write("lessons/R1-01/handouts/One.pdf", HandoutBytes.Pdf("one"));
        await RunAsync();
        var withOne = Version();

        Assert.NotEqual(withNone, withOne);
        Assert.Equal(withOne, (await RunAsync(), Version()).Item2);
    }

    [Fact]
    public async Task AHandoutsFolderOfNoLesson_IsAWarning()
    {
        Write("lessons/R9-99/handouts/Stray.pdf", HandoutBytes.Pdf());

        var result = await RunAsync();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("R9-99/handouts"));
    }
}
