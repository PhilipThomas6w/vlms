using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The app-hosted lesson reads of <see cref="LessonLibraryService"/> (docs/adr/0012-app-hosted-lessons.md decisions 5–7):
/// the lesson page, lesson pictures, the Teacher's Information and teacher's notes shown to Teachers, Admins, the National
/// Administrator and Approvers only (docs/adr/0015; enforced in the service, for each role), and what a device needs to save
/// a converted lesson's page, which for a student or parent never includes the notes or their pictures.
/// </summary>
public sealed class LessonPageServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public LessonPageServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-lesson-page-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var context = CreateContext(new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 5, Code = "HERALD", Name = "Herald", TeachersInformation = "## For teachers\n\n![Room](room.jpg)" });
        context.Lessons.Add(new Lesson
        {
            Id = 10, RankId = 1, Code = "R5-01", Title = "Prosphora", Order = 1, IsActive = true,
            Body = "## Questions\n\n![Loaves](loaves.jpg)\n\n<script>alert(1)</script>", ContentVersion = "v1",
            TeachersNotes = "## Lesson plan\n\nTEACHER-ONLY-PLAN\n\n![Seating plan](seating.jpg)\n\n![Loaves](loaves.jpg)"
        });
        context.Lessons.Add(new Lesson { Id = 11, RankId = 1, Code = "R5-02", Title = "Unconverted", Order = 2, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 12, RankId = 1, Code = "R5-03", Title = "Retired", Order = 3, IsActive = false, Body = "Old", ContentVersion = "v0" });
        context.LessonFiles.Add(new LessonFile { Id = 100, LessonId = 10, Order = 1, FileName = "Prosphora.docx", BlobKey = "lessons/R5-01/Prosphora.docx" });
        context.LessonFiles.Add(new LessonFile { Id = 101, LessonId = 11, Order = 1, FileName = "Liturgy.docx", BlobKey = "lessons/R5-02/Liturgy.docx" });
        context.LessonPictures.Add(new LessonPicture { Id = 20, LessonId = 10, Order = 1, FileName = "loaves.jpg", BlobKey = "lesson-pictures/R5-01/loaves.jpg" });
        context.LessonPictures.Add(new LessonPicture { Id = 21, LessonId = 12, Order = 1, FileName = "old.jpg", BlobKey = "lesson-pictures/R5-03/old.jpg" });
        context.LessonPictures.Add(new LessonPicture
        {
            Id = 22, LessonId = 10, Order = 2, FileName = "seating.jpg", BlobKey = "lesson-pictures/R5-01/seating.jpg", TeachersNotesOnly = true
        });
        context.TeachersInformationPictures.Add(new TeachersInformationPicture { Id = 30, RankId = 1, Order = 1, FileName = "room.jpg", BlobKey = "teachers-information/HERALD/room.jpg" });
        context.SaveChanges();

        _blobs.UploadAsync("lesson-pictures/R5-01/loaves.jpg", [1, 2]).Wait();
        _blobs.UploadAsync("lesson-pictures/R5-03/old.jpg", [3]).Wait();
        _blobs.UploadAsync("teachers-information/HERALD/room.jpg", [4]).Wait();
        _blobs.UploadAsync("lesson-pictures/R5-01/seating.jpg", [5]).Wait();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private VlmsDbContext CreateContext(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private async Task<T> AsAsync<T>(Role role, Func<LessonLibraryService, Task<T>> act)
    {
        var user = new FakeCurrentUserContext(5, role);
        using var context = CreateContext(user);
        return await act(new LessonLibraryService(context, _blobs, user));
    }

    // Approvers see both since docs/adr/0015 (flagged assumption: they must read the notes to review a suggestion).
    public static TheoryData<Role> Staff => new() { Role.Teacher, Role.Admin, Role.NationalAdministrator, Role.Approver };

    public static TheoryData<Role> NotStaff => new() { Role.Student, Role.Parent };

    [Theory]
    [MemberData(nameof(Staff))]
    public async Task LessonPage_ForStaff_CarriesTheTeachersInformation(Role role)
    {
        var page = await AsAsync(role, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.NotNull(page.TeachersInformationHtml);
        Assert.Contains("For teachers", page.TeachersInformationHtml);
        Assert.Contains("src=\"/lessons/teachers-information/pictures/30\"", page.TeachersInformationHtml);
    }

    [Theory]
    [MemberData(nameof(NotStaff))]
    public async Task LessonPage_ForStudentsAndParents_HasNoTeachersInformation(Role role)
    {
        var page = await AsAsync(role, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.Null(page.TeachersInformationHtml);
        Assert.DoesNotContain("For teachers", page.Html);
    }

    [Theory]
    [MemberData(nameof(Staff))]
    public async Task LessonPage_ForStaff_CarriesTheTeachersNotes_WithANotesOnlyPictureAtTheStaffAddress(Role role)
    {
        var page = await AsAsync(role, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.True(page.ShowsTeachersMaterial);
        Assert.NotNull(page.TeachersNotesHtml);
        Assert.Contains("TEACHER-ONLY-PLAN", page.TeachersNotesHtml);
        Assert.Contains("src=\"/lessons/teachers-notes/pictures/22\"", page.TeachersNotesHtml);
        // A picture the text uses too keeps its ordinary address.
        Assert.Contains("src=\"/lessons/pictures/20\"", page.TeachersNotesHtml);
        Assert.DoesNotContain("TEACHER-ONLY-PLAN", page.Html);
        // The notes-only picture is never one the device saves under the lesson-picture address.
        Assert.Equal([20], page.PictureIds);
    }

    [Theory]
    [MemberData(nameof(NotStaff))]
    public async Task LessonPage_ForStudentsAndParents_HasNoTeachersNotes_NorAnyTraceOfTheirPictures(Role role)
    {
        var page = await AsAsync(role, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.False(page.ShowsTeachersMaterial);
        Assert.Null(page.TeachersNotesHtml);
        Assert.Null(page.TeachersInformationHtml);
        Assert.DoesNotContain("TEACHER-ONLY-PLAN", page.Html);
        Assert.DoesNotContain("seating", page.Html);
        Assert.DoesNotContain("teachers-notes", page.Html);
        Assert.Equal([20], page.PictureIds);
    }

    [Theory]
    [MemberData(nameof(Staff))]
    public async Task LessonPage_ForStaff_WithNoNotesAndNoTeachersInformation_StillShowsTheSections(Role role)
    {
        using (var context = CreateContext(new FakeCurrentUserContext(1, Role.Admin)))
        {
            var rank = context.Ranks.Single(r => r.Id == 1);
            context.Entry(rank).Property(r => r.TeachersInformation).CurrentValue = null;
            var lesson = context.Lessons.Single(l => l.Id == 10);
            context.Entry(lesson).Property(l => l.TeachersNotes).CurrentValue = null;
            context.SaveChanges();
        }

        var page = await AsAsync(role, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.True(page.ShowsTeachersMaterial);
        Assert.Null(page.TeachersNotesHtml);
        Assert.Null(page.TeachersInformationHtml);
        Assert.Equal("HERALD", page.RankCode);
    }

    [Theory]
    [MemberData(nameof(Staff))]
    public async Task TeachersNotesPicture_IsServedToStaff(Role role)
    {
        var picture = await AsAsync(role, s => s.OpenTeachersNotesPictureAsync(22));

        Assert.NotNull(picture);
        Assert.Equal([5], picture.Content);
    }

    [Theory]
    [MemberData(nameof(NotStaff))]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task TeachersNotesPicture_IsRefusedToEveryoneElse(Role role) =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AsAsync(role, s => s.OpenTeachersNotesPictureAsync(22)));

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Teacher)]
    public async Task NotesOnlyPicture_IsNeverServedByTheLessonPictureAddress(Role role) =>
        Assert.Null(await AsAsync(role, s => s.OpenPictureAsync(22)));

    [Fact]
    public async Task LessonPage_HasTheTitleRankAndSafeText_WithPicturesAtTheirAppUrls()
    {
        var page = await AsAsync(Role.Student, s => s.GetLessonPageAsync("R5-01"));

        Assert.NotNull(page);
        Assert.Equal(("Prosphora", "HERALD: Herald", "v1"), (page.Title, page.Rank, page.ContentVersion));
        Assert.Equal((0, 0), (page.RankPosition, page.Position));
        Assert.Contains("src=\"/lessons/pictures/20\"", page.Html);
        Assert.DoesNotContain("<script", page.Html);
        Assert.Equal([20], page.PictureIds);
    }

    [Theory]
    [InlineData("R5-02")] // not converted
    [InlineData("R5-03")] // retired
    [InlineData("NOPE")]
    public async Task LessonPage_IsNull_ForAnUnconvertedRetiredOrUnknownLesson(string code) =>
        Assert.Null(await AsAsync(Role.Teacher, s => s.GetLessonPageAsync(code)));

    [Fact]
    public async Task LessonPage_RefusesARoleOutsideTheLibrary() =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AsAsync(Role.SafeguardingOfficer, s => s.GetLessonPageAsync("R5-01")));

    [Fact]
    public async Task Picture_IsServedForAnActiveLessonOnly()
    {
        var picture = await AsAsync(Role.Parent, s => s.OpenPictureAsync(20));
        Assert.NotNull(picture);
        Assert.Equal("loaves.jpg", picture.FileName);
        Assert.Equal([1, 2], picture.Content);

        Assert.Null(await AsAsync(Role.Parent, s => s.OpenPictureAsync(21)));
        Assert.Null(await AsAsync(Role.Parent, s => s.OpenPictureAsync(999)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AsAsync(Role.SafeguardingOfficer, s => s.OpenPictureAsync(20)));
    }

    [Theory]
    [MemberData(nameof(Staff))]
    public async Task TeachersInformationPicture_IsServedToStaff(Role role)
    {
        var picture = await AsAsync(role, s => s.OpenTeachersInformationPictureAsync(30));

        Assert.NotNull(picture);
        Assert.Equal([4], picture.Content);
    }

    [Theory]
    [MemberData(nameof(NotStaff))]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task TeachersInformationPicture_IsRefusedToEveryoneElse(Role role) =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AsAsync(role, s => s.OpenTeachersInformationPictureAsync(30)));

    [Fact]
    public async Task Library_MarksConvertedLessons_WithTheirVersionAndPictures()
    {
        var library = await AsAsync(Role.Student, s => s.GetLibraryAsync());

        var lessons = Assert.Single(library).Lessons;
        var converted = lessons.Single(l => l.Code == "R5-01");
        var plain = lessons.Single(l => l.Code == "R5-02");
        Assert.Equal((true, "v1"), (converted.HasPage, converted.ContentVersion));
        Assert.Equal([20], converted.PictureIds);
        Assert.Equal((false, null), (plain.HasPage, plain.ContentVersion));
        Assert.Empty(plain.PictureIds);
    }

    [Fact]
    public async Task SavedLessons_AConvertedLessonIsItsPageAndPictures_NotItsFiles()
    {
        var saved = await AsAsync(Role.Student, s => s.GetSavedLessonsAsync([10, 11]));

        var converted = saved.Single(l => l.Id == 10);
        Assert.Equal(("/lessons/R5-01", "v1"), (converted.Page, converted.Version));
        Assert.Equal([20], converted.PictureIds);
        Assert.Empty(converted.Files);

        var plain = saved.Single(l => l.Id == 11);
        Assert.Null(plain.Page);
        Assert.Equal([101], plain.Files.Select(f => f.Id));
    }
}
