using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the lesson library's read side (functional.md FR-006): ranks and lessons in delivery
/// order with readable tags, inactive lessons hidden, files served by id only, and the role check
/// enforced by the service itself. Same SQLite-in-memory pattern as <see cref="LessonProposalServiceTests"/>.
/// </summary>
public sealed class LessonLibraryServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public LessonLibraryServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-library-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var context = CreateContext(new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        Seed(context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private VlmsDbContext CreateContext(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private LessonLibraryService CreateService(VlmsDbContext context, ICurrentUserContext user) =>
        new(context, _blobs, user);

    private void Seed(VlmsDbContext context)
    {
        // Ranks deliberately inserted out of order; rank 3 has no lessons.
        context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Second" });
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "First" });
        context.Ranks.Add(new Rank { Id = 3, Order = 3, Code = "R3", Name = "Empty" });

        // Lesson orders out of insertion order; lesson 13 is retired.
        context.Lessons.Add(new Lesson { Id = 11, RankId = 1, Code = "L-B", Title = "Second lesson", Order = 2, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 12, RankId = 1, Code = "L-A", Title = "First lesson", Order = 1, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 13, RankId = 1, Code = "L-X", Title = "Retired lesson", Order = 3, IsActive = false });
        context.Lessons.Add(new Lesson { Id = 21, RankId = 2, Code = "L-C", Title = "Third lesson", Order = 1, IsActive = true });

        context.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = 12, Tag = LessonTag.OldTestament });
        context.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = 12, Tag = LessonTag.ChurchLife });

        context.LessonFiles.Add(new LessonFile { Id = 102, LessonId = 12, Order = 2, FileName = "Handout.pdf", BlobKey = "lessons/12/handout.pdf" });
        context.LessonFiles.Add(new LessonFile { Id = 101, LessonId = 12, Order = 1, FileName = "Lesson.docx", BlobKey = "lessons/12/lesson.docx" });
        context.LessonFiles.Add(new LessonFile { Id = 131, LessonId = 13, Order = 1, FileName = "Old.pdf", BlobKey = "lessons/13/old.pdf" });
        context.LessonFiles.Add(new LessonFile { Id = 111, LessonId = 11, Order = 1, FileName = "Lost.pdf", BlobKey = "lessons/11/lost.pdf" });
        context.SaveChanges();

        _blobs.UploadAsync("lessons/12/lesson.docx", [1, 2, 3]).Wait();
        _blobs.UploadAsync("lessons/12/handout.pdf", [4, 5]).Wait();
        _blobs.UploadAsync("lessons/13/old.pdf", [6]).Wait();
    }

    public static TheoryData<Role> AllowedRoles => new() { Role.Teacher, Role.Approver, Role.Admin, Role.Student, Role.Parent };

    [Theory]
    [MemberData(nameof(AllowedRoles))]
    public async Task GetLibraryAsync_AllowedRole_ReturnsRanksAndLessonsInDeliveryOrder(Role role)
    {
        var user = new FakeCurrentUserContext(5, role);
        using var context = CreateContext(user);

        var library = await CreateService(context, user).GetLibraryAsync();

        Assert.Equal(["R1", "R2", "R3"], library.Select(r => r.Code));
        Assert.Equal(["L-A", "L-B"], library[0].Lessons.Select(l => l.Code));
        Assert.Equal(["L-C"], library[1].Lessons.Select(l => l.Code));
    }

    [Fact]
    public async Task GetLibraryAsync_RankWithNoLessons_IsListedWithAnEmptyLessonList()
    {
        var user = new FakeCurrentUserContext(5, Role.Student);
        using var context = CreateContext(user);

        var library = await CreateService(context, user).GetLibraryAsync();

        Assert.Empty(library.Single(r => r.Code == "R3").Lessons);
    }

    [Fact]
    public async Task GetLibraryAsync_ExcludesInactiveLessons()
    {
        var user = new FakeCurrentUserContext(5, Role.Teacher);
        using var context = CreateContext(user);

        var library = await CreateService(context, user).GetLibraryAsync();

        Assert.DoesNotContain(library.SelectMany(r => r.Lessons), l => l.Code == "L-X");
    }

    [Fact]
    public async Task GetLibraryAsync_ShowsReadableTagsAndFilesInOrder()
    {
        var user = new FakeCurrentUserContext(5, Role.Parent);
        using var context = CreateContext(user);

        var lesson = (await CreateService(context, user).GetLibraryAsync())[0].Lessons[0];

        Assert.Equal(["Church life", "Old Testament"], lesson.Tags);
        Assert.Equal([101, 102], lesson.Files.Select(f => f.Id));
        Assert.Equal(["Lesson.docx", "Handout.pdf"], lesson.Files.Select(f => f.FileName));
    }

    [Fact]
    public void LessonTagNames_HasADisplayNameForEveryTag()
    {
        foreach (var tag in Enum.GetValues<LessonTag>())
        {
            Assert.False(string.IsNullOrWhiteSpace(LessonTagNames.Display(tag)));
        }
    }

    [Theory]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task GetLibraryAsync_DisallowedRole_Throws(Role role)
    {
        var user = new FakeCurrentUserContext(5, role);
        using var context = CreateContext(user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(context, user).GetLibraryAsync());
    }

    [Fact]
    public async Task GetLibraryAsync_NoRoles_Throws()
    {
        var user = new FakeCurrentUserContext(5);
        using var context = CreateContext(user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(context, user).GetLibraryAsync());
    }

    [Theory]
    [MemberData(nameof(AllowedRoles))]
    public async Task OpenFileAsync_AllowedRole_ReturnsNameAndBytes(Role role)
    {
        var user = new FakeCurrentUserContext(5, role);
        using var context = CreateContext(user);

        var file = await CreateService(context, user).OpenFileAsync(101);

        Assert.NotNull(file);
        Assert.Equal("Lesson.docx", file!.FileName);
        Assert.Equal([1, 2, 3], file.Content);
    }

    [Fact]
    public async Task OpenFileAsync_SafeguardingOfficerOnly_Throws()
    {
        var user = new FakeCurrentUserContext(5, Role.SafeguardingOfficer);
        using var context = CreateContext(user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(context, user).OpenFileAsync(101));
    }

    [Fact]
    public async Task OpenFileAsync_FileOfInactiveLesson_ReturnsNull()
    {
        var user = new FakeCurrentUserContext(5, Role.Admin);
        using var context = CreateContext(user);

        Assert.Null(await CreateService(context, user).OpenFileAsync(131));
    }

    [Fact]
    public async Task OpenFileAsync_UnknownId_ReturnsNull()
    {
        var user = new FakeCurrentUserContext(5, Role.Admin);
        using var context = CreateContext(user);

        Assert.Null(await CreateService(context, user).OpenFileAsync(9999));
    }

    [Theory]
    [MemberData(nameof(AllowedRoles))]
    public async Task GetSavedLessonsAsync_AllowedRole_ReturnsTheLessonsAsAskedWithRankAndFiles(Role role)
    {
        var user = new FakeCurrentUserContext(5, role);
        using var context = CreateContext(user);

        var saved = await CreateService(context, user).GetSavedLessonsAsync([12, 21]);

        Assert.Equal([12, 21], saved.Select(l => l.Id));
        var first = saved[0];
        Assert.Equal("First lesson", first.Title);
        Assert.Equal("R1: First", first.Rank);
        Assert.Equal([101, 102], first.Files.Select(f => f.Id));
    }

    [Fact]
    public async Task GetSavedLessonsAsync_UnknownAndRetiredIds_AreLeftOut()
    {
        var user = new FakeCurrentUserContext(5, Role.Teacher);
        using var context = CreateContext(user);

        var saved = await CreateService(context, user).GetSavedLessonsAsync([13, 9999, 11]);

        Assert.Equal([11], saved.Select(l => l.Id));
    }

    [Fact]
    public async Task GetSavedLessonsAsync_SafeguardingOfficerOnly_Throws()
    {
        var user = new FakeCurrentUserContext(5, Role.SafeguardingOfficer);
        using var context = CreateContext(user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(context, user).GetSavedLessonsAsync([12]));
    }

    [Fact]
    public async Task OpenFileAsync_BlobMissingFromStorage_ReturnsNull()
    {
        var user = new FakeCurrentUserContext(5, Role.Admin);
        using var context = CreateContext(user);

        Assert.Null(await CreateService(context, user).OpenFileAsync(111));
    }
}
