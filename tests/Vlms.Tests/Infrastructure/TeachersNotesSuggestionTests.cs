using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// docs/adr/0015 through <see cref="LessonSuggestionService"/>: a lesson suggestion carries the teacher's notes beside the
/// text (decision 2), reviewed, approved and kept in the history with it, a picture only the notes use marked staff-only;
/// and a rank's Teacher's Information is its own suggestion (decision 4), one open per rank, with its own numbered
/// versions and history for Approvers and Admins. Same SQLite-in-memory pattern as <see cref="LessonSuggestionServiceTests"/>.
/// </summary>
public sealed class TeachersNotesSuggestionTests : IDisposable
{
    private const int Admin = 1, TeacherOne = 10, TeacherTwo = 11, Approver = 20;
    private const int LessonId = 1, RankId = 1;
    private const string LessonCode = "R1-01", RankCode = "RECRUIT";
    private const string Body = "## Questions\n\n- Why are these two saints so well known?\n\n![The icon screen](screen.jpg)\n";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public TeachersNotesSuggestionTests()
    {
        _connectionString = $"Data Source=file:vlms-notes-suggestion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = RankId, Order = 1, Code = RankCode, Name = "Recruit" });
        db.Ranks.Add(new Rank
        {
            Id = 2, Order = 2, Code = "HERALD", Name = "Herald", TeachersInformation = "## For teachers\n\n![Room](room.jpg)\n"
        });
        db.TeachersInformationPictures.Add(new TeachersInformationPicture { Id = 1, RankId = 2, Order = 1, FileName = "room.jpg", BlobKey = "teachers-information/HERALD/room.jpg" });
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
        _blobs.UploadAsync("teachers-information/HERALD/room.jpg", [4]).Wait();
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

    private static SuggestionInput Edit(string? notes, int? id = null, string body = Body, string? reason = "Add teacher's notes.") =>
        new(id, LessonId, RankId, LessonChangeKind.Edit, "St John Chrysostom and St Basil", 1, [LessonTag.Saints], body, reason,
            TeachersNotes: notes);

    private static SuggestionInput Information(int rankId, string body, int? id = null, string? reason = "Add guidance.") =>
        new(id, null, rankId, LessonChangeKind.TeachersInformation, "", 0, [], body, reason);

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

    // --- Teacher's notes in a lesson suggestion (decision 2) -------------------------------------------------------

    [Fact]
    public async Task ASuggestion_CarriesTheNotes_ThroughReviewApprovalAndHistory_WithANotesOnlyPictureStaffOnly()
    {
        var editor = await As(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.Equal("", editor!.Draft!.TeachersNotes);

        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit("Draft notes.\n")));
        var picture = await As(TeacherOne, Teacher, s => s.UploadPictureAsync(id, "Seating plan.png", new MemoryStream(Png())));
        var notes = $"## Lesson plan\n\nNOTES-ONE\n\n![Seating plan]({picture.FileName})\n";
        await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(notes, id)));

        var review = await As(Approver, ApproverRoles, s => s.GetReviewAsync(id));
        Assert.NotNull(review!.NotesTrackedHtml);
        Assert.Contains("<ins>", review.NotesTrackedHtml);
        Assert.Contains("NOTES-ONE", review.NotesTrackedHtml);
        Assert.Contains("NOTES-ONE", review.NotesFinalHtml);
        Assert.DoesNotContain("NOTES-ONE", review.TrackedHtml);

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));
        Assert.Equal(2, published.Number);

        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Id == LessonId);
            Assert.Equal(notes, lesson.TeachersNotes);
            Assert.Equal(Body, lesson.Body);
            Assert.NotEqual("v0", lesson.ContentVersion);
            var pictures = await db.LessonPictures.Where(p => p.LessonId == LessonId).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([("screen.jpg", false), (picture.FileName, true)], pictures.Select(p => (p.FileName, p.TeachersNotesOnly)));
            var version = await db.LessonVersions.Include(v => v.Pictures).SingleAsync(v => v.LessonId == LessonId && v.Number == 2);
            Assert.Equal(notes, version.TeachersNotes);
            Assert.Equal(["screen.jpg", picture.FileName], version.Pictures.OrderBy(p => p.Order).Select(p => p.FileName));
        }

        var history = await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));
        var latest = history!.Versions[0];
        Assert.Equal(2, latest.Number);
        Assert.NotNull(latest.NotesChangesHtml);
        Assert.Contains("NOTES-ONE", latest.NotesChangesHtml);
        Assert.Null(history.Versions[1].NotesChangesHtml);

        // The next suggestion starts from the notes, the notes-only picture at its staff address.
        var next = await As(TeacherTwo, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.Equal(notes, next!.Draft!.TeachersNotes);
        Assert.StartsWith("/lessons/teachers-notes/pictures/", next.Draft.PictureUrls[picture.FileName]);
        Assert.StartsWith("/lessons/pictures/", next.Draft.PictureUrls["screen.jpg"]);

        // Staff read the notes on the page; students do not, nor the picture.
        Assert.Contains("NOTES-ONE", (await PageAs(Role.Teacher))!.TeachersNotesHtml);
        var studentPage = (await PageAs(Role.Student))!;
        Assert.Null(studentPage.TeachersNotesHtml);
        Assert.Single(studentPage.PictureIds);
    }

    [Fact]
    public async Task ChangingOnlyTheNotes_IsANewVersion_WithANewContentVersion_SoSavedCopiesRefresh()
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
        Assert.Equal(("Second notes.\n", Body), (lesson.TeachersNotes, lesson.Body));
        Assert.NotEqual(before, lesson.ContentVersion);
        var history = await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));
        Assert.Contains("Second", history!.Versions[0].NotesChangesHtml);
    }

    [Fact]
    public async Task ClearingTheNotes_StoresNone()
    {
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("Some notes.\n")));
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("  \n")));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.Null((await db.Lessons.SingleAsync(l => l.Id == LessonId)).TeachersNotes);
    }

    [Fact]
    public async Task APictureInTheNotes_MustBeOneOfTheSuggestions()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.SubmitAsync(Edit("![Elsewhere](https://example.com/x.png)\n"))));
        Assert.Contains("is not one of this lesson's pictures", ex.Message);

        var noDescription = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherTwo, Teacher, s => s.SubmitAsync(Edit("![](screen.jpg)\n", id: null))));
        Assert.Contains("needs a description", noDescription.Message);
    }

    // --- Rank Teacher's Information (decision 4) -------------------------------------------------------------------

    [Fact]
    public async Task TeachersInformation_RoundTrip_SuggestReviewApproveAndHistory()
    {
        var editor = await As(TeacherOne, Teacher, s => s.GetTeachersInformationEditorAsync(RankCode));
        Assert.True(editor!.ForTeachersInformation);
        Assert.Equal((LessonChangeKind.TeachersInformation, "", (int?)null), (editor.Draft!.Kind, editor.Draft.Body, editor.CurrentVersionNumber));

        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Information(RankId, "## For teachers\n\nTI-ONE\n")));

        var queue = await As(Approver, ApproverRoles, s => s.GetQueueAsync());
        var item = Assert.Single(queue);
        Assert.Equal((LessonSuggestionService.TeachersInformationTitle, "Recruit", LessonChangeKind.TeachersInformation), (item.LessonTitle, item.RankName, item.Kind));
        var review = await As(Approver, ApproverRoles, s => s.GetReviewAsync(id));
        Assert.Contains("<ins>", review!.TrackedHtml);
        Assert.Contains("TI-ONE", review.TrackedHtml);
        Assert.False(review.IsStale);
        Assert.Null(review.NotesTrackedHtml);

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));
        Assert.Equal((1, LessonVersionKind.ApprovedSuggestion, true), (published.Number, published.Kind, published.ForTeachersInformation));
        Assert.Equal("/curriculum/teachers-information/RECRUIT/history", published.HistoryUrl);

        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            Assert.Equal("## For teachers\n\nTI-ONE\n", (await db.Ranks.SingleAsync(r => r.Id == RankId)).TeachersInformation);
            var version = await db.TeachersInformationVersions.SingleAsync(v => v.RankId == RankId);
            Assert.Equal((1, TeacherOne, (int?)Approver, (int?)id), (version.Number, version.SuggestedByUserId, version.ApprovedByUserId, version.SuggestionId));
            // The lesson itself is untouched: no lesson version was added.
            Assert.False(await db.LessonVersions.AnyAsync());
        }

        foreach (var roles in new[] { ApproverRoles, new[] { Role.Admin } })
        {
            var history = await As(Admin, roles, s => s.GetTeachersInformationHistoryAsync(RankCode));
            var entry = Assert.Single(history!.Versions);
            Assert.Equal(("Teacher One", "Approver One"), (entry.SuggestedBy, entry.ApprovedBy));
        }
        Assert.Contains("TI-ONE", (await PageAs(Role.Approver))!.TeachersInformationHtml);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    public async Task TeachersInformationHistory_IsForApproversAndAdminsOnly(Role role) =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetTeachersInformationHistoryAsync(RankCode)));

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    public async Task TeachersInformation_MayBeSuggestedByTeachersAndApproversOnly(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetTeachersInformationEditorAsync(RankCode)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.SaveDraftAsync(Information(RankId, "x"))));
    }

    [Fact]
    public async Task OnlyOneTeachersInformationSuggestion_MayBeOpenPerRank_ApartFromLessonSuggestions()
    {
        await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Information(RankId, "Mine.\n")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(TeacherTwo, Teacher, s => s.SaveDraftAsync(Information(RankId, "Other.\n"))));
        Assert.Equal("A change to this Teacher's Information is already in progress.", ex.Message);
        var editor = await As(TeacherTwo, Teacher, s => s.GetTeachersInformationEditorAsync(RankCode));
        Assert.Equal("Teacher One", editor!.BlockedBy!.AuthorName);
        Assert.Null(editor.Draft);

        // Another rank, and a lesson of the same rank, are not blocked.
        await As(TeacherTwo, Teacher, s => s.SaveDraftAsync(Information(2, "Herald.\n")));
        await As(TeacherTwo, Teacher, s => s.SaveDraftAsync(Edit("Notes.\n")));
    }

    [Fact]
    public void TheDatabase_AllowsOnlyOneOpenTeachersInformationSuggestionPerRank()
    {
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        LessonSuggestion Open(int id, LessonSuggestionStatus status) => new()
        {
            Id = id, RankId = RankId, Kind = LessonChangeKind.TeachersInformation, Status = status, AuthorUserId = TeacherOne,
            Title = LessonSuggestionService.TeachersInformationTitle, Order = 0, Body = "B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.LessonSuggestions.Add(Open(1, LessonSuggestionStatus.SentBack));
        db.LessonSuggestions.Add(Open(2, LessonSuggestionStatus.Draft));
        db.SaveChanges();

        db.LessonSuggestions.Add(Open(3, LessonSuggestionStatus.Submitted));
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task ExistingTeachersInformation_BecomesVersionOne_AndPublishingNowKeepsItsPictures()
    {
        var editor = await As(Approver, ApproverRoles, s => s.GetTeachersInformationEditorAsync("HERALD"));
        Assert.StartsWith("## For teachers", editor!.Draft!.Body);
        Assert.Equal("/lessons/teachers-information/pictures/1", editor.Draft.PictureUrls["room.jpg"]);

        var published = await As(Approver, ApproverRoles, s => s.PublishNowAsync(Information(2, "## For teachers\n\nTI-TWO\n\n![Room](room.jpg)\n", reason: null)));

        Assert.Equal((2, LessonVersionKind.PublishedDirectly), (published.Number, published.Kind));
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var versions = await db.TeachersInformationVersions.Include(v => v.Pictures).Where(v => v.RankId == 2).OrderBy(v => v.Number).ToListAsync();
        Assert.Equal([LessonVersionKind.Converted, LessonVersionKind.PublishedDirectly], versions.Select(v => v.Kind));
        Assert.All(versions, v => Assert.Equal(["teachers-information/HERALD/room.jpg"], v.Pictures.Select(p => p.BlobKey)));
        var live = await db.TeachersInformationPictures.SingleAsync(p => p.RankId == 2);
        Assert.Equal((1, "teachers-information/HERALD/room.jpg"), (live.Id, live.BlobKey));

        var history = await As(Approver, ApproverRoles, s => s.GetTeachersInformationHistoryAsync("HERALD"));
        Assert.Contains("TI-TWO", history!.Versions[0].ChangesHtml);
        Assert.Equal("Approver One", history.Versions[0].PublishedBy);
        var picture = await As(Approver, ApproverRoles, s => s.OpenTeachersInformationVersionPictureAsync(versions[1].Pictures.Single().Id));
        Assert.Equal([4], picture!.Content);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            As(TeacherOne, Teacher, s => s.OpenTeachersInformationVersionPictureAsync(versions[1].Pictures.Single().Id)));
    }

    [Fact]
    public async Task ASentBackTeachersInformationSuggestion_CarriesOnAsTheAuthorsNewDraft()
    {
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Information(RankId, "First try.\n")));
        await As(Approver, ApproverRoles, s => s.SendBackAsync(id, "Say more."));

        var editor = await As(TeacherOne, Teacher, s => s.GetTeachersInformationEditorAsync(RankCode));

        Assert.Equal(("First try.\n", (int?)id), (editor!.Draft!.Body, editor.Draft.RevisesSuggestionId));
        Assert.Equal("Say more.", editor.SentBack!.Comment);
        var history = await As(Admin, [Role.Admin], s => s.GetTeachersInformationHistoryAsync(RankCode));
        Assert.Equal("Say more.", Assert.Single(history!.SentBack).Comment);
    }

    [Fact]
    public async Task ATeachersInformationSuggestion_CannotBecomeALessonOne()
    {
        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Information(RankId, "TI.\n")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit("x", id: id))));
    }
}
