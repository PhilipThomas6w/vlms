using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// docs/adr/0015 and its amendment through <see cref="LessonSuggestionService"/>: a lesson suggestion carries the lesson's
/// Teacher's Information beside the text, reviewed, approved and kept in the history with it, a picture only it uses
/// marked staff-only. There is no rank-wide Teacher's Information. Same SQLite-in-memory pattern as
/// <see cref="LessonSuggestionServiceTests"/>.
/// </summary>
public sealed class TeachersInformationSuggestionTests : IDisposable
{
    private const int Admin = 1, TeacherOne = 10, TeacherTwo = 11, Approver = 20;
    private const int LessonId = 1, RankId = 1;
    private const string LessonCode = "R1-01", RankCode = "RECRUIT";
    private const string Body = "## Questions\n\n- Why are these two saints so well known?\n\n![The icon screen](screen.jpg)\n";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public TeachersInformationSuggestionTests()
    {
        _connectionString = $"Data Source=file:vlms-notes-suggestion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = RankId, Order = 1, Code = RankCode, Name = "Recruit" });
        foreach (var (id, name) in new[] { (Admin, "Admin One"), (TeacherOne, "Teacher One"), (TeacherTwo, "Teacher Two"), (Approver, "Approver One") })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        }
        db.Lessons.Add(new Lesson
        {
            Id = LessonId, RankId = RankId, Code = LessonCode, Title = "St John Chrysostom and St Basil", Order = 1, IsActive = true,
            Body = Body, ContentVersion = "v0"
        });
        db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = LessonId, Tag = LessonTag.Saints });
        db.LessonPictures.Add(new LessonPicture { Id = 1, LessonId = LessonId, Order = 1, FileName = "screen.jpg", BlobKey = "lesson-pictures/R1-01/abc/screen.jpg" });
        db.SaveChanges();
        _blobs.UploadAsync("lesson-pictures/R1-01/abc/screen.jpg", [1, 2, 3]).Wait();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext Context(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private async Task<T> As<T>(int userId, Role[] roles, Func<LessonSuggestionService, Task<T>> action)
    {
        var user = new FakeCurrentUserContext(userId, roles);
        using var db = Context(user);
        return await action(new LessonSuggestionService(db, user, _blobs));
    }

    private Task As(int userId, Role[] roles, Func<LessonSuggestionService, Task> action) =>
        As(userId, roles, async s => { await action(s); return 0; });

    private async Task<LessonPage?> PageAs(Role role)
    {
        var user = new FakeCurrentUserContext(Admin, role);
        using var db = Context(user);
        return await new LessonLibraryService(db, _blobs, user).GetLessonPageAsync(LessonCode);
    }

    private static readonly Role[] Teacher = [Role.Teacher];
    private static readonly Role[] ApproverRoles = [Role.Approver];

    private static SuggestionInput Edit(string? notes, int? id = null, string body = Body, string? reason = "Add Teacher's Information.") =>
        new(id, LessonId, RankId, LessonChangeKind.Edit, "St John Chrysostom and St Basil", 1, [LessonTag.Saints], body, reason,
            TeachersInformation: notes);

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(20, 10);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Gold);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // --- Teacher's Information in a lesson suggestion ---------------------------------------------------------------------

    [Fact]
    public async Task ASuggestion_CarriesTheTeachersInformation_ThroughReviewApprovalAndHistory_WithAPictureOnlyItUsesStaffOnly()
    {
        var editor = await As(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.Equal("", editor!.Draft!.TeachersInformation);

        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit("Draft notes.\n")));
        var picture = await As(TeacherOne, Teacher, s => s.UploadPictureAsync(id, "Seating plan.png", new MemoryStream(Png())));
        var notes = $"## Lesson plan\n\nNOTES-ONE\n\n![Seating plan]({picture.FileName})\n";
        await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(notes, id)));

        var review = await As(Approver, ApproverRoles, s => s.GetReviewAsync(id));
        Assert.NotNull(review!.InformationTrackedHtml);
        Assert.Contains("<ins>", review.InformationTrackedHtml);
        Assert.Contains("NOTES-ONE", review.InformationTrackedHtml);
        Assert.Contains("NOTES-ONE", review.InformationFinalHtml);
        Assert.DoesNotContain("NOTES-ONE", review.TrackedHtml);

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));
        Assert.Equal(2, published.Number);

        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Id == LessonId);
            Assert.Equal(notes, lesson.TeachersInformation);
            Assert.Equal(Body, lesson.Body);
            Assert.NotEqual("v0", lesson.ContentVersion);
            var pictures = await db.LessonPictures.Where(p => p.LessonId == LessonId).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([("screen.jpg", false), (picture.FileName, true)], pictures.Select(p => (p.FileName, p.TeachersInformationOnly)));
            var version = await db.LessonVersions.Include(v => v.Pictures).SingleAsync(v => v.LessonId == LessonId && v.Number == 2);
            Assert.Equal(notes, version.TeachersInformation);
            Assert.Equal(["screen.jpg", picture.FileName], version.Pictures.OrderBy(p => p.Order).Select(p => p.FileName));
        }

        var history = await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));
        var latest = history!.Versions[0];
        Assert.Equal(2, latest.Number);
        Assert.NotNull(latest.InformationChangesHtml);
        Assert.Contains("NOTES-ONE", latest.InformationChangesHtml);
        Assert.Null(history.Versions[1].InformationChangesHtml);

        // The next suggestion starts from it, its own picture at the staff address.
        var next = await As(TeacherTwo, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.Equal(notes, next!.Draft!.TeachersInformation);
        Assert.StartsWith("/lessons/teachers-information/pictures/", next.Draft.PictureUrls[picture.FileName]);
        Assert.StartsWith("/lessons/pictures/", next.Draft.PictureUrls["screen.jpg"]);

        // Staff read it on the page; students do not, nor its picture.
        Assert.Contains("NOTES-ONE", (await PageAs(Role.Teacher))!.TeachersInformationHtml);
        var studentPage = (await PageAs(Role.Student))!;
        Assert.Null(studentPage.TeachersInformationHtml);
        Assert.Single(studentPage.PictureIds);
    }

    [Fact]
    public async Task ChangingOnlyTheTeachersInformation_IsANewVersion_WithANewContentVersion_SoSavedCopiesRefresh()
    {
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("First notes.\n")));
        string before;
        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            before = (await db.Lessons.SingleAsync(l => l.Id == LessonId)).ContentVersion!;
        }

        var published = await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("Second notes.\n")));

        Assert.Equal(3, published.Number);
        using var after = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var lesson = await after.Lessons.SingleAsync(l => l.Id == LessonId);
        Assert.Equal(("Second notes.\n", Body), (lesson.TeachersInformation, lesson.Body));
        Assert.NotEqual(before, lesson.ContentVersion);
        var history = await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));
        Assert.Contains("Second", history!.Versions[0].InformationChangesHtml);
    }

    [Fact]
    public async Task ClearingTheTeachersInformation_StoresNone()
    {
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("Some notes.\n")));
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("  \n")));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.Null((await db.Lessons.SingleAsync(l => l.Id == LessonId)).TeachersInformation);
    }

    [Fact]
    public async Task APictureInTheTeachersInformation_MustBeOneOfTheSuggestions()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.SubmitAsync(Edit("![Elsewhere](https://example.com/x.png)\n"))));
        Assert.Contains("is not one of this lesson's pictures", ex.Message);

        var noDescription = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherTwo, Teacher, s => s.SubmitAsync(Edit("![](screen.jpg)\n", id: null))));
        Assert.Contains("needs a description", noDescription.Message);
    }
}
