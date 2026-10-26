using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Lesson suggestions, review and history (FR-007, docs/adr/0013): who may do what, enforced by
/// <see cref="LessonSuggestionService"/> itself; one open suggestion per lesson; self-approval; the stale-base guard;
/// "Publish now" only while direct publishing is allowed, and that setting's audit; versions numbered and kept; the live
/// lesson and its content version updated on publishing; students and parents seeing only the published lesson.
/// Real <see cref="VlmsDbContext"/> on a named shared-cache SQLite in-memory database kept alive by one anchor connection.
/// </summary>
public sealed class LessonSuggestionServiceTests : IDisposable
{
    private const int Admin = 1, TeacherOne = 10, TeacherTwo = 11, Approver = 20, National = 30, Student = 40, Parent = 50;
    private const int LessonId = 1;
    private const string LessonCode = "R1-01";
    private const string OriginalBody = "## Questions\n\n- Why are these two saints so well known?\n\n![The icon screen](screen.jpg)\n";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public LessonSuggestionServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-suggestion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "RECRUIT", Name = "Recruit" });
        foreach (var (id, name) in new[] { (Admin, "Admin One"), (TeacherOne, "Teacher One"), (TeacherTwo, "Teacher Two"),
                     (Approver, "Approver One"), (National, "National One"), (Student, "Student One"), (Parent, "Parent One") })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        }
        db.Lessons.Add(new Lesson
        {
            Id = LessonId, RankId = 1, Code = LessonCode, Title = "St John Chrysostom and St Basil", Order = 1, IsActive = true,
            Body = OriginalBody, ContentVersion = "v0"
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

    private static readonly Role[] Teacher = [Role.Teacher];
    private static readonly Role[] ApproverRoles = [Role.Approver];

    private static SuggestionInput Edit(string body, string? reason = "Add a question.", int? id = null,
        LessonChangeKind kind = LessonChangeKind.Edit, string title = "St John Chrysostom and St Basil") =>
        new(id, LessonId, 1, kind, title, 1, [LessonTag.Saints], body, reason);

    private static string ChangedBody => OriginalBody.Replace("well known?", "well known?\n- Which two Liturgies are named after them?");

    // --- Roles -----------------------------------------------------------------------------------------------------

    public static TheoryData<Role> NonSuggesterRoles => new() { Role.Admin, Role.Student, Role.Parent, Role.SafeguardingOfficer, Role.NationalAdministrator };

    [Theory]
    [MemberData(nameof(NonSuggesterRoles))]
    public async Task OnlyTeachersAndApprovers_MaySuggest(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.SaveDraftAsync(Edit(ChangedBody))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.SubmitAsync(Edit(ChangedBody))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetEditorAsync(LessonCode)));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    public async Task TeachersAndApprovers_MaySaveAndSubmit(Role role)
    {
        var id = await As(TeacherOne, [role], s => s.SubmitAsync(Edit(ChangedBody)));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var suggestion = await db.LessonSuggestions.SingleAsync(x => x.Id == id);
        Assert.Equal((LessonSuggestionStatus.Submitted, 1, TeacherOne), (suggestion.Status, suggestion.BaseVersionNumber, suggestion.AuthorUserId));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    public async Task OnlyApprovers_MayReviewApproveSendBackOrPublishNow(Role role)
    {
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(ChangedBody)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetQueueAsync()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetReviewAsync(id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.ApproveAsync(id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.SendBackAsync(id, "No.")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.PublishNowAsync(Edit(ChangedBody))));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    public async Task OnlyApproversAndAdmins_MayReadHistory(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetHistoryAsync(LessonCode)));
        Assert.NotNull(await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode)));
        Assert.NotNull(await As(Approver, ApproverRoles, s => s.GetHistoryAsync(LessonCode)));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Admin)]
    public async Task OnlyNationalAdministrators_MayChangeOrReadThePublishingSetting(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.SetDirectPublishingAllowedAsync(false)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Admin, [role], s => s.GetPublishingSettingAsync()));
    }

    [Fact]
    public async Task ADraft_IsTheAuthorsOwn()
    {
        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit(ChangedBody)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(TeacherTwo, Teacher, s => s.SaveDraftAsync(Edit("Mine now.\n", id: id))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(TeacherTwo, Teacher, s => s.DiscardAsync(id)));
        await As(TeacherOne, Teacher, s => s.DiscardAsync(id));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.False(await db.LessonSuggestions.AnyAsync());
    }

    // --- One open suggestion ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyOneSuggestion_MayBeOpenForALesson()
    {
        await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit(ChangedBody)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(TeacherTwo, Teacher, s => s.SaveDraftAsync(Edit("Other.\n"))));
        Assert.Equal("A change to this lesson is already in progress.", ex.Message);

        var editor = await As(TeacherTwo, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.NotNull(editor!.BlockedBy);
        Assert.Equal("Teacher One", editor.BlockedBy!.AuthorName);
        Assert.Null(editor.Draft);
        var page = await As(TeacherTwo, Teacher, s => s.GetLessonPageAsync(LessonId));
        Assert.False(page.Open!.IsMine);
    }

    [Fact]
    public void TheDatabase_AllowsOnlyOneOpenSuggestionPerLesson()
    {
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        LessonSuggestion Open(int id, LessonSuggestionStatus status) => new()
        {
            Id = id, LessonId = LessonId, RankId = 1, Kind = LessonChangeKind.Edit, Status = status, BaseVersionNumber = 1,
            AuthorUserId = TeacherOne, Title = "T", Order = 1, Body = "B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.LessonSuggestions.Add(Open(1, LessonSuggestionStatus.SentBack));
        db.LessonSuggestions.Add(Open(2, LessonSuggestionStatus.Approved));
        db.LessonSuggestions.Add(Open(3, LessonSuggestionStatus.Draft));
        db.SaveChanges();

        db.LessonSuggestions.Add(Open(4, LessonSuggestionStatus.Submitted));
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    // --- Submit, approve, versions ---------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_RequiresAReason()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(ChangedBody, reason: " "))));
        Assert.Equal("Give a reason for the change.", ex.Message);
    }

    [Fact]
    public async Task Approving_AddsTheNextVersion_UpdatesTheLesson_AndChangesItsContentVersion()
    {
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(ChangedBody, title: "Two holy hierarchs")));

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));

        Assert.Equal((2, LessonVersionKind.ApprovedSuggestion), (published.Number, published.Kind));
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var lesson = await db.Lessons.SingleAsync(l => l.Id == LessonId);
        Assert.Equal(ChangedBody, lesson.Body);
        Assert.Equal("Two holy hierarchs", lesson.Title);
        Assert.NotEqual("v0", lesson.ContentVersion);
        var versions = await db.LessonVersions.Include(v => v.Pictures).Where(v => v.LessonId == LessonId).OrderBy(v => v.Number).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        Assert.Equal((LessonVersionKind.Converted, OriginalBody), (versions[0].Kind, versions[0].Body));
        Assert.Equal((TeacherOne, (int?)Approver, (int?)id), (versions[1].SuggestedByUserId, versions[1].ApprovedByUserId, versions[1].SuggestionId));
        Assert.Equal(lesson.ContentVersion, versions[1].ContentVersion);
        // Every version keeps its pictures.
        Assert.All(versions, v => Assert.Equal(["screen.jpg"], v.Pictures.Select(p => p.FileName)));
        Assert.Equal(LessonSuggestionStatus.Approved, (await db.LessonSuggestions.SingleAsync(s => s.Id == id)).Status);
    }

    [Fact]
    public async Task AnApprover_MayApproveTheirOwnSuggestion()
    {
        var id = await As(Approver, ApproverRoles, s => s.SubmitAsync(Edit(ChangedBody)));

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));

        Assert.Equal(2, published.Number);
    }

    [Fact]
    public async Task VersionsAreNumberedInTurn_AndAllKept()
    {
        for (var i = 0; i < 3; i++)
        {
            var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit($"## Text {i}\n")));
            await As(Approver, ApproverRoles, s => s.ApproveAsync(id));
        }

        var history = await As(Approver, ApproverRoles, s => s.GetHistoryAsync(LessonCode));

        Assert.Equal([4, 3, 2, 1], history!.Versions.Select(v => v.Number));
        Assert.True(history.Versions[0].IsCurrent);
        Assert.Contains("<ins>", history.Versions[0].ChangesHtml);
        Assert.Contains("<del>", history.Versions[0].ChangesHtml);
    }

    [Fact]
    public async Task ASuggestionBasedOnAnOlderVersion_CannotBeApproved()
    {
        var stale = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(ChangedBody)));
        // The lesson moves on underneath it.
        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            db.LessonVersions.Add(new LessonVersion
            {
                Id = await db.NextIdAsync<LessonVersion>(), LessonId = LessonId, Number = 2, Kind = LessonVersionKind.Converted,
                Title = "St John Chrysostom and St Basil", Order = 1, IsActive = true, Body = "Newer.\n", PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var review = await As(Approver, ApproverRoles, s => s.GetReviewAsync(stale));
        Assert.True(review!.IsStale);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(Approver, ApproverRoles, s => s.ApproveAsync(stale)));
        Assert.Contains("has changed since this suggestion was started", ex.Message);
    }

    [Fact]
    public async Task ADraft_IsNeverBasedOnAVersionLaterThanTheOneTheEditorOpened()
    {
        var first = await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit(ChangedBody)));
        Assert.Equal(2, first.Number);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit("Started on version 1.\n") with { BaseVersionNumber = 1 })));
        Assert.Contains("has changed since you opened it", ex.Message);
    }

    [Fact]
    public async Task SendingBack_RequiresAComment_KeepsIt_AndFreesTheLesson()
    {
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(ChangedBody)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => As(Approver, ApproverRoles, s => s.SendBackAsync(id, "  ")));
        await As(Approver, ApproverRoles, s => s.SendBackAsync(id, "Give the feast as 1 January."));

        var history = await As(Approver, ApproverRoles, s => s.GetHistoryAsync(LessonCode));
        var sentBack = Assert.Single(history!.SentBack);
        Assert.Equal(("Teacher One", "Approver One", "Give the feast as 1 January."), (sentBack.SuggestedBy, sentBack.SentBackBy, sentBack.Comment));
        Assert.Single(history.Versions);

        // Someone else may now suggest; the author carries on from the suggestion that was sent back.
        var mine = await As(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode));
        Assert.Equal((null, id), (mine!.Draft!.Id, mine.Draft.RevisesSuggestionId));
        Assert.Equal("Give the feast as 1 January.", mine.SentBack!.Comment);
        Assert.Null((await As(TeacherTwo, Teacher, s => s.GetEditorAsync(LessonCode)))!.BlockedBy);
    }

    // --- Publish now and the setting -------------------------------------------------------------------------------

    [Fact]
    public async Task PublishNow_PublishesAtOnce_WithNoReasonNeeded()
    {
        var published = await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit(ChangedBody, reason: null)));

        Assert.Equal((2, LessonVersionKind.PublishedDirectly), (published.Number, published.Kind));
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var version = await db.LessonVersions.SingleAsync(v => v.Number == 2);
        Assert.Equal(((int?)Approver, (int?)null), (version.PublishedByUserId, version.ApprovedByUserId));
        Assert.Equal(ChangedBody, (await db.Lessons.SingleAsync()).Body);
    }

    [Fact]
    public async Task ReplacingTheWholeLesson_IsRecordedAsReplacedInFull_AndShownSideBySide()
    {
        await As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit("## A new lesson\n\nAll new text.\n", kind: LessonChangeKind.ReplaceInFull)));

        var history = await As(Approver, ApproverRoles, s => s.GetHistoryAsync(LessonCode));
        var latest = history!.Versions[0];
        Assert.Equal(LessonVersionKind.ReplacedInFull, latest.Kind);
        Assert.Null(latest.ChangesHtml);
        Assert.Contains("All new text.", latest.AfterHtml);
        Assert.Contains("well known?", latest.BeforeHtml);
    }

    [Fact]
    public async Task PublishNow_IsRefused_WhileReviewIsRequired()
    {
        await As(National, [Role.NationalAdministrator], s => s.SetDirectPublishingAllowedAsync(false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(Approver, ApproverRoles, s => s.PublishNowAsync(Edit(ChangedBody))));
        Assert.Equal("Review is required for every change: submit it for review instead.", ex.Message);
        Assert.False((await As(Approver, ApproverRoles, s => s.GetEditorAsync(LessonCode)))!.CanPublishNow);
        Assert.True((await As(Approver, ApproverRoles, s => s.GetEditorAsync(LessonCode)))!.Draft is not null);
    }

    [Fact]
    public async Task ChangingTheSetting_IsAudited()
    {
        Assert.True(await As(Approver, ApproverRoles, s => s.IsDirectPublishingAllowedAsync()));

        await As(National, [Role.NationalAdministrator], s => s.SetDirectPublishingAllowedAsync(false));
        await As(National, [Role.NationalAdministrator], s => s.SetDirectPublishingAllowedAsync(false));
        await As(National, [Role.NationalAdministrator], s => s.SetDirectPublishingAllowedAsync(true));

        var setting = await As(National, [Role.NationalAdministrator], s => s.GetPublishingSettingAsync());
        Assert.True(setting.DirectPublishingAllowed);
        Assert.Equal([true, false], setting.Changes.Select(c => c.DirectPublishingAllowed));
        Assert.All(setting.Changes, c => Assert.Equal("National One", c.ChangedBy));
    }

    [Fact]
    public async Task OnlyApprovers_SeePublishNow()
    {
        Assert.False((await As(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode)))!.CanPublishNow);
        Assert.True((await As(Approver, ApproverRoles, s => s.GetEditorAsync(LessonCode)))!.CanPublishNow);
    }

    // --- New lesson, retire ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewLesson_IsCreatedWhenApproved_AsVersionOne()
    {
        var input = new SuggestionInput(null, null, 1, LessonChangeKind.NewLesson, "The Sign of the Cross", 2, [LessonTag.Prayers],
            "## How we cross ourselves\n", "New lesson.");
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(input));

        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(id));

        Assert.Equal(1, published.Number);
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var lesson = await db.Lessons.Include(l => l.Tags).SingleAsync(l => l.Code == published.LessonCode);
        Assert.Equal(("The Sign of the Cross", 2, true), (lesson.Title, lesson.Order, lesson.IsActive));
        Assert.Equal([LessonTag.Prayers], lesson.Tags.Select(t => t.Tag));
    }

    [Fact]
    public async Task Retiring_HidesTheLesson_AndKeepsItsVersions()
    {
        var id = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(OriginalBody, kind: LessonChangeKind.Retire, reason: "No longer taught.")));
        await As(Approver, ApproverRoles, s => s.ApproveAsync(id));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.False((await db.Lessons.SingleAsync()).IsActive);
        Assert.Equal(2, await db.LessonVersions.CountAsync());
        Assert.Null(await As(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode)));
    }

    // --- Pictures --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APicture_MustHaveAltText_AndBeInTheSuggestion()
    {
        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit(OriginalBody)));
        var picture = await As(TeacherOne, Teacher, s => s.UploadPictureAsync(id, "Loaves.png", new MemoryStream(Png())));
        Assert.Equal("loaves.png", picture.FileName);

        var noAlt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(OriginalBody + "\n![](loaves.png)\n", id: id))));
        Assert.Contains("descri", noAlt.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(OriginalBody + "\n![Bread](missing.png)\n", id: id))));

        var ok = await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit(OriginalBody + "\n![Three loaves](loaves.png)\n", id: id)));
        var published = await As(Approver, ApproverRoles, s => s.ApproveAsync(ok));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var pictures = await db.LessonPictures.Where(p => p.LessonId == LessonId).Select(p => p.FileName).ToListAsync();
        Assert.Equal(["loaves.png", "screen.jpg"], pictures.Order());
        var version = await db.LessonVersions.Include(v => v.Pictures).SingleAsync(v => v.Number == published.Number);
        Assert.All(version.Pictures, p => Assert.True(_blobs.TryGetBlob(p.BlobKey, out _), p.BlobKey));
    }

    [Fact]
    public async Task OnlyTheAuthor_MayAddPicturesToADraft()
    {
        var id = await As(TeacherOne, Teacher, s => s.SaveDraftAsync(Edit(OriginalBody)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            As(TeacherTwo, Teacher, s => s.UploadPictureAsync(id, "x.png", new MemoryStream(Png()))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(TeacherOne, Teacher, s => s.UploadPictureAsync(id, "x.png", new MemoryStream([1, 2, 3]))));
    }

    // --- Students and parents --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    public async Task StudentsAndParents_SeeOnlyThePublishedLesson(Role role)
    {
        await As(TeacherOne, Teacher, s => s.SubmitAsync(Edit("## Not yet approved\n")));

        var page = await As(Student, [role], s => s.GetLessonPageAsync(LessonId));
        Assert.Equal((false, false, (OpenSuggestion?)null), (page.CanSuggest, page.CanReadHistory, page.Open));

        var user = new FakeCurrentUserContext(Student, role);
        using var db = Context(user);
        var lesson = await new LessonLibraryService(db, _blobs, user).GetLessonPageAsync(LessonCode);
        Assert.Contains("well known?", lesson!.Html);
        Assert.DoesNotContain("Not yet approved", lesson.Html);
    }

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
}
