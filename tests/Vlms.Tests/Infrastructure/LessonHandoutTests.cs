using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Lesson handouts (docs/adr/0016), enforced by <see cref="LessonSuggestionService"/> and <see cref="LessonLibraryService"/>
/// themselves: the upload's rules (type by content, size, the draft's author only, a safe stored name), the suggestion,
/// review, approval and history round trip with handouts added, renamed and removed, version handouts for Approvers and
/// Admins only, and the audience: everyone who can see the lesson sees and downloads its current handouts, offline included.
/// </summary>
public sealed class LessonHandoutTests : IDisposable
{
    private const int Admin = 1, TeacherOne = 10, TeacherTwo = 11, Approver = 20;
    private const int LessonId = 1, ExistingFileId = 5;
    private const string LessonCode = "R1-01";
    private const string ExistingKey = "lessons/R1-01/Fasting dial.pdf";
    private const string Body = "## Text\n\nRead.\n";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public LessonHandoutTests()
    {
        _connectionString = $"Data Source=file:vlms-handout-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "RECRUIT", Name = "Recruit" });
        foreach (var (id, name) in new[] { (Admin, "Admin One"), (TeacherOne, "Teacher One"), (TeacherTwo, "Teacher Two"), (Approver, "Approver One") })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        }
        db.Lessons.Add(new Lesson { Id = LessonId, RankId = 1, Code = LessonCode, Title = "Fasting", Order = 1, IsActive = true, Body = Body, ContentVersion = "v0" });
        db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = LessonId, Tag = LessonTag.Saints });
        db.LessonFiles.Add(new LessonFile { Id = ExistingFileId, LessonId = LessonId, Order = 1, FileName = "Fasting dial.pdf", DisplayName = "Fasting dial", BlobKey = ExistingKey });
        db.SaveChanges();
        _blobs.UploadAsync(ExistingKey, HandoutBytes.Pdf("dial")).Wait();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext Context(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private async Task<T> Suggest<T>(int userId, Role[] roles, Func<LessonSuggestionService, Task<T>> action)
    {
        var user = new FakeCurrentUserContext(userId, roles);
        using var db = Context(user);
        return await action(new LessonSuggestionService(db, user, _blobs));
    }

    private async Task<T> Library<T>(Role role, Func<LessonLibraryService, Task<T>> action)
    {
        var user = new FakeCurrentUserContext(Admin, role);
        using var db = Context(user);
        return await action(new LessonLibraryService(db, _blobs, user));
    }

    private static readonly Role[] Teacher = [Role.Teacher];
    private static readonly Role[] ApproverRoles = [Role.Approver];

    private static SuggestionInput Input(int? id = null, IReadOnlyList<HandoutInput>? handouts = null) =>
        new(id, LessonId, 1, LessonChangeKind.Edit, "Fasting", 1, [LessonTag.Saints], Body, "New handout.", Handouts: handouts);

    private Task<int> DraftAsync(int author = TeacherOne) => Suggest(author, Teacher, s => s.SaveDraftAsync(Input()));

    private Task<UploadedHandout> UploadAsync(int suggestionId, string name, byte[] content, int user = TeacherOne, Role[]? roles = null) =>
        Suggest(user, roles ?? Teacher, s => s.UploadHandoutAsync(suggestionId, name, new MemoryStream(content)));

    private int StoredHandouts()
    {
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        return db.LessonSuggestionHandouts.Count();
    }

    // --- The upload ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewDraft_StartsWithTheLessonsHandouts()
    {
        var id = await DraftAsync();

        var editor = await Suggest(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode));

        Assert.Equal(id, editor!.Draft!.Id);
        var handout = Assert.Single(editor.Draft.Handouts);
        Assert.Equal(("Fasting dial", ExistingKey), (handout.DisplayName, handout.Key));
    }

    public static TheoryData<string, byte[]> Spoofed => new()
    {
        { "worksheet.pdf", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>") },
        { "worksheet.docx", HandoutBytes.Zip() },
        { "worksheet.png", HandoutBytes.Jpeg() },
        { "worksheet.jpg", HandoutBytes.Png() },
        { "worksheet.html", HandoutBytes.Pdf() },
        { "worksheet.svg", HandoutBytes.Png() },
        { "worksheet", HandoutBytes.Pdf() },
    };

    [Theory]
    [MemberData(nameof(Spoofed))]
    public async Task Upload_AFileThatIsNotWhatItsNameSays_OrNotAnAllowedType_IsRefused(string name, byte[] content)
    {
        var id = await DraftAsync();
        var before = StoredHandouts();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync(id, name, content));

        Assert.Equal(LessonHandoutFiles.TypesMessage, error.Message);
        Assert.Equal(before, StoredHandouts());
    }

    [Theory]
    [InlineData("Worksheet.pdf")]
    [InlineData("Worksheet.docx")]
    [InlineData("Worksheet.jpg")]
    [InlineData("Worksheet.jpeg")]
    [InlineData("Worksheet.PNG")]
    public async Task Upload_EachAllowedType_IsAccepted(string name)
    {
        var id = await DraftAsync();
        var content = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" => HandoutBytes.Pdf("w"),
            ".docx" => HandoutBytes.Docx("w"),
            ".png" => HandoutBytes.Png("w"),
            _ => HandoutBytes.Jpeg("w")
        };

        var handout = await UploadAsync(id, name, content);

        Assert.Equal("Worksheet", handout.DisplayName);
    }

    [Fact]
    public async Task Upload_OverTwentyMegabytes_IsRefused()
    {
        var id = await DraftAsync();
        var big = new byte[LessonHandoutFiles.MaxBytes + 1];
        HandoutBytes.Pdf().CopyTo(big, 0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync(id, "big.pdf", big));

        Assert.Contains("20 MB", error.Message);
    }

    [Fact]
    public async Task Upload_IsForTheDraftsAuthorOnly()
    {
        var id = await DraftAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UploadAsync(id, "a.pdf", HandoutBytes.Pdf(), TeacherTwo));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UploadAsync(id, "a.pdf", HandoutBytes.Pdf(), Approver, ApproverRoles));
        foreach (var role in new[] { Role.Admin, Role.Student, Role.Parent, Role.SafeguardingOfficer, Role.NationalAdministrator })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UploadAsync(id, "a.pdf", HandoutBytes.Pdf(), TeacherOne, [role]));
        }
        Assert.Equal(1, StoredHandouts());
    }

    [Fact]
    public async Task Upload_ToASubmittedSuggestion_IsRefused()
    {
        var id = await Suggest(TeacherOne, Teacher, s => s.SubmitAsync(Input()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync(id, "a.pdf", HandoutBytes.Pdf()));
    }

    [Fact]
    public async Task Upload_KeepsNoPathFromTheClient_AndStoresUnderAKeyNamingItsContent()
    {
        var id = await DraftAsync();

        var handout = await UploadAsync(id, "..\\..\\etc/  Colouring: page?.PDF", HandoutBytes.Pdf("colour"));

        Assert.Equal(("Colouring page", "Colouring page.pdf"), (handout.DisplayName, handout.FileName));
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var stored = await db.LessonSuggestionHandouts.SingleAsync(h => h.Id == handout.Id);
        Assert.Matches($"^lesson-handouts/suggestions/{id}/[0-9a-f]{{16}}\\.pdf$", stored.BlobKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync(id, "again.pdf", HandoutBytes.Pdf("colour")));
    }

    [Fact]
    public async Task ADisplayName_IsRequired()
    {
        var id = await DraftAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Suggest(TeacherOne, Teacher, s => s.SaveDraftAsync(Input(id, [new HandoutInput(ExistingKey, "  ")]))));
    }

    [Fact]
    public async Task ASuggestionsHandout_IsForItsAuthorAndApprovers()
    {
        var id = await DraftAsync();
        var handout = await UploadAsync(id, "a.pdf", HandoutBytes.Pdf("a"));

        Assert.NotNull(await Suggest(TeacherOne, Teacher, s => s.OpenSuggestionHandoutAsync(id, handout.Id)));
        Assert.NotNull(await Suggest(Approver, ApproverRoles, s => s.OpenSuggestionHandoutAsync(id, handout.Id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Suggest(TeacherTwo, Teacher, s => s.OpenSuggestionHandoutAsync(id, handout.Id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Suggest(Admin, [Role.Student], s => s.OpenSuggestionHandoutAsync(id, handout.Id)));
    }

    // --- Review, approval and history -------------------------------------------------------------------------------

    [Fact]
    public async Task ReviewApproveAndHistory_CarryTheHandouts_AndAnUnchangedHandoutKeepsItsId()
    {
        var id = await DraftAsync();
        var added = await UploadAsync(id, "Colouring page.png", HandoutBytes.Png("colour"));
        var editor = await Suggest(TeacherOne, Teacher, s => s.GetEditorAsync(LessonCode));
        var keys = editor!.Draft!.Handouts.ToDictionary(h => h.DisplayName, h => h.Key);
        await Suggest(TeacherOne, Teacher, s => s.SubmitAsync(Input(id,
            [new HandoutInput(keys["Fasting dial"], "Fasting dial template"), new HandoutInput(keys["Colouring page"], "Colouring page")])));

        var review = await Suggest(Approver, ApproverRoles, s => s.GetReviewAsync(id));
        var change = Assert.Single(review!.SettingChanges, c => c.Label == "Handouts");
        Assert.Contains("<del>Fasting dial</del> <ins>Fasting dial template</ins> (renamed)", change.Html);
        Assert.Contains("<ins>Colouring page</ins> (added)", change.Html);
        Assert.Equal(["Fasting dial template", "Colouring page"], review.Handouts.Select(h => h.DisplayName));

        var published = await Suggest(Approver, ApproverRoles, s => s.ApproveAsync(id));

        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            var live = await db.LessonFiles.Where(f => f.LessonId == LessonId).OrderBy(f => f.Order).ToListAsync();
            Assert.Equal([("Fasting dial template", ExistingFileId), ("Colouring page", live[1].Id)], live.Select(f => (f.DisplayName, f.Id)));
            Assert.NotEqual(ExistingFileId, live[1].Id);
            var versions = await db.LessonVersions.Include(v => v.Handouts).OrderBy(v => v.Number).ToListAsync();
            Assert.Equal([1, 2], versions.Select(v => v.Number));
            Assert.Equal(["Fasting dial"], versions[0].Handouts.Select(h => h.DisplayName));
            Assert.Equal(["Fasting dial template", "Colouring page"], versions[1].Handouts.OrderBy(h => h.Order).Select(h => h.DisplayName));
            Assert.NotEqual("v0", (await db.Lessons.SingleAsync()).ContentVersion);
        }

        var history = await Suggest(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));
        var current = history!.Versions.Single(v => v.Number == published.Number);
        Assert.Contains(current.SettingChanges, c => c.Label == "Handouts" && c.Html.Contains("(added)"));
        Assert.Equal(["Fasting dial template", "Colouring page"], current.Handouts.Select(h => h.DisplayName));
        Assert.Equal(["Fasting dial"], history.Versions.Single(v => v.Number == 1).Handouts.Select(h => h.DisplayName));
    }

    [Fact]
    public async Task ARemovedHandout_LeavesTheLesson_ButStaysDownloadableFromTheHistory()
    {
        await Suggest(Approver, ApproverRoles, s => s.PublishNowAsync(Input(handouts: [])));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.Empty(await db.LessonFiles.ToListAsync());
        Assert.Null(await Library(Role.Student, l => l.OpenFileAsync(ExistingFileId)));
        var history = await Suggest(Approver, ApproverRoles, s => s.GetHistoryAsync(LessonCode));
        Assert.Contains(history!.Versions.Single(v => v.Number == 2).SettingChanges, c => c.Html.Contains("<del>Fasting dial</del> (removed)"));
        var old = await db.LessonVersionHandouts.SingleAsync();
        var content = await Suggest(Admin, [Role.Admin], s => s.OpenVersionHandoutAsync(old.Id));
        Assert.Equal(HandoutBytes.Pdf("dial"), content!.Content);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    public async Task AVersionsHandout_IsForApproversAndAdminsOnly(Role role)
    {
        await Suggest(Approver, ApproverRoles, s => s.PublishNowAsync(Input()));
        int handoutId;
        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            handoutId = (await db.LessonVersionHandouts.FirstAsync()).Id;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Suggest(Admin, [role], s => s.OpenVersionHandoutAsync(handoutId)));
        Assert.NotNull(await Suggest(Approver, ApproverRoles, s => s.OpenVersionHandoutAsync(handoutId)));
        Assert.NotNull(await Suggest(Admin, [Role.Admin], s => s.OpenVersionHandoutAsync(handoutId)));
    }

    // --- Audience --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Admin)]
    [InlineData(Role.NationalAdministrator)]
    public async Task EveryoneWhoSeesTheLesson_SeesAndDownloadsItsHandouts_OfflineCopyIncluded(Role role)
    {
        var page = await Library(role, l => l.GetLessonPageAsync(LessonCode));
        var handout = Assert.Single(page!.Handouts);
        Assert.Equal((ExistingFileId, "Fasting dial", "/lessons/files/5"), (handout.Id, handout.DisplayName, handout.Url));

        var file = await Library(role, l => l.OpenFileAsync(ExistingFileId));
        Assert.Equal(HandoutBytes.Pdf("dial"), file!.Content);

        // A saved (offline) copy of the converted lesson keeps its handouts as well as its page and pictures.
        var saved = Assert.Single(await Library(role, l => l.GetSavedLessonsAsync([LessonId])));
        Assert.Equal("/lessons/R1-01", saved.Page);
        Assert.Equal([ExistingFileId], saved.Files.Select(f => f.Id));
    }

    [Fact]
    public async Task NobodyOutsideTheLibraryRoles_SeesOrDownloadsHandouts()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Library(Role.SafeguardingOfficer, l => l.GetLessonPageAsync(LessonCode)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Library(Role.SafeguardingOfficer, l => l.OpenFileAsync(ExistingFileId)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Library(Role.SafeguardingOfficer, l => l.GetSavedLessonsAsync([LessonId])));
    }

    [Fact]
    public void HandoutChanges_AreHtmlEncoded()
    {
        var html = LessonSuggestionService.HandoutChangesHtml([], [("k", "<script>x</script>")]);

        Assert.Equal("<ul><li><ins>&lt;script&gt;x&lt;/script&gt;</ins> (added)</li></ul>", html);
    }
}
