using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Pending lesson files (FR-007, TC-017): who may open them, that approval makes them live, that
/// rejected files never become reachable through the library, and the Approver's comparison.
/// </summary>
public sealed class LessonProposalFilesTests : IDisposable
{
    private const string LiveKey = "lessons/1/old.docx";
    private const string PendingKey = "lessons/pending/10/abc/New.pdf";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public LessonProposalFilesTests()
    {
        _connectionString = $"Data Source=file:vlms-propfiles-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(1, Role.Admin);
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        foreach (var (id, name) in new[] { (10, "Teacher One"), (11, "Teacher Two"), (20, "Approver"), (30, "Student"), (31, "Parent") })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"u{id}@example.com" });
        }

        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = 1, Tag = LessonTag.Prayers });
        db.LessonFiles.Add(new LessonFile { Id = 1, LessonId = 1, Order = 1, FileName = "Old.docx", BlobKey = LiveKey });
        db.SaveChanges();

        _blobs.UploadAsync(LiveKey, [1]).Wait();
        _blobs.UploadAsync(PendingKey, [2]).Wait();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext Context(int? userId, params Role[] roles) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            new FakeCurrentUserContext(userId, roles));

    private LessonProposalService Service(VlmsDbContext db, int userId, params Role[] roles) =>
        new(db, new FakeCurrentUserContext(userId, roles), _blobs);

    private static ProposedLessonContent Edit(params (string Name, string Key)[] files) => new()
    {
        Code = "L1", Title = "Knots", Order = 1, Tags = [LessonTag.Prayers], IsActive = true,
        Files = files.Select(f => new ProposedLessonFile { FileName = f.Name, BlobKey = f.Key }).ToList()
    };

    private async Task<int> ProposeAsync(params (string Name, string Key)[] files)
    {
        using var db = Context(10, Role.Teacher);
        return (await Service(db, 10, Role.Teacher).ProposeAsync(1, LessonChangeType.Edit, Edit(files))).Id;
    }

    private async Task<byte[]?> OpenAsync(int proposalId, int userId, params Role[] roles)
    {
        using var db = Context(userId, roles);
        return (await Service(db, userId, roles).OpenProposalFileAsync(proposalId, 1))?.Content;
    }

    [Fact]
    public async Task Proposer_CanOpenPendingFile()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        Assert.Equal([2], await OpenAsync(id, 10, Role.Teacher));
    }

    [Fact]
    public async Task Approver_CanOpenPendingFile()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        Assert.Equal([2], await OpenAsync(id, 20, Role.Approver));
    }

    [Fact]
    public async Task AnotherTeacher_CannotOpenPendingFile()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OpenAsync(id, 11, Role.Teacher));
    }

    [Theory]
    [InlineData(Role.Student, 30)]
    [InlineData(Role.Parent, 31)]
    [InlineData(Role.Admin, 1)]
    public async Task StudentParentAndAdmin_CannotOpenPendingFile(Role role, int userId)
    {
        var id = await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OpenAsync(id, userId, role));
    }

    [Fact]
    public async Task PendingFile_IsNotServedByTheLibrary_BeforeApproval()
    {
        await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        using var db = Context(30, Role.Student);
        var library = await new LessonLibraryService(db, _blobs, new FakeCurrentUserContext(30, Role.Student)).GetLibraryAsync();

        Assert.Equal(["Old.docx"], library.Single().Lessons.Single().Files.Select(f => f.FileName));
    }

    [Fact]
    public async Task Approval_PointsTheLiveFilesAtThePendingBlob_InTheProposedOrder()
    {
        var id = await ProposeAsync(("New.pdf", PendingKey), ("Old.docx", LiveKey));

        using (var db = Context(20, Role.Approver))
        {
            await Service(db, 20, Role.Approver).ApproveAsync(id);
        }

        using var verify = Context(30, Role.Student);
        var files = await verify.LessonFiles.Where(f => f.LessonId == 1).OrderBy(f => f.Order).ToListAsync();
        Assert.Equal([PendingKey, LiveKey], files.Select(f => f.BlobKey));

        var opened = await new LessonLibraryService(verify, _blobs, new FakeCurrentUserContext(30, Role.Student))
            .OpenFileAsync(files[0].Id);
        Assert.Equal([2], opened!.Content);
    }

    [Fact]
    public async Task RejectedFile_NeverBecomesReachableThroughTheLibrary()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey), ("New.pdf", PendingKey));

        using (var db = Context(20, Role.Approver))
        {
            await Service(db, 20, Role.Approver).RejectAsync(id, "No.");
        }

        using var verify = Context(30, Role.Student);
        Assert.DoesNotContain(await verify.LessonFiles.ToListAsync(), f => f.BlobKey == PendingKey);
        // The proposer can still open it, to revise the proposal.
        Assert.Equal([2], await OpenAsync(id, 10, Role.Teacher));
    }

    [Fact]
    public async Task Propose_WithKeyOfAnotherTeachersPendingUpload_IsRefused()
    {
        using var db = Context(11, Role.Teacher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, 11, Role.Teacher)
            .ProposeAsync(1, LessonChangeType.Edit, Edit(("New.pdf", PendingKey))));
    }

    [Theory]
    [InlineData("certificates/100/1.pdf")]
    [InlineData("lessons/2/other-lesson.docx")]
    [InlineData("lessons/pending/10/../../certificates/x.pdf")]
    public async Task Propose_WithArbitraryBlobKey_IsRefused(string key)
    {
        using var db = Context(10, Role.Teacher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, 10, Role.Teacher)
            .ProposeAsync(1, LessonChangeType.Edit, Edit(("x.pdf", key))));
    }

    [Fact]
    public async Task UnknownProposalOrIndex_IsNotFound()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey));

        Assert.Null(await OpenAsync(9999, 20, Role.Approver));
        using var db = Context(20, Role.Approver);
        Assert.Null(await Service(db, 20, Role.Approver).OpenProposalFileAsync(id, 5));
    }

    [Fact]
    public async Task Review_MarksAddedRemovedAndChangedFields()
    {
        var content = new ProposedLessonContent
        {
            Code = "L1", Title = "Knots revised", Order = 1, Tags = [LessonTag.Prayers], IsActive = true,
            Files = [new ProposedLessonFile { FileName = "New.pdf", BlobKey = PendingKey }]
        };
        int id;
        using (var db = Context(10, Role.Teacher))
        {
            id = (await Service(db, 10, Role.Teacher).ProposeAsync(1, LessonChangeType.Edit, content)).Id;
        }

        using var approverDb = Context(20, Role.Approver);
        var review = await Service(approverDb, 20, Role.Approver).GetReviewAsync(id);

        Assert.True(review.HasCurrent);
        Assert.True(review.Fields.Single(f => f.Label == "Title").Changed);
        Assert.False(review.Fields.Single(f => f.Label == "Code").Changed);
        Assert.Equal(ReviewFileChange.Added, review.ProposedFiles.Single().Change);
        Assert.Equal(ReviewFileChange.Removed, review.CurrentFiles.Single().Change);
        Assert.Equal("/lessons/files/1", review.CurrentFiles.Single().Url);
        Assert.Equal($"/curriculum/proposals/{id}/files/0", review.ProposedFiles.Single().Url);
        Assert.True(review.FilesChanged);
    }

    [Fact]
    public async Task Review_DetectsReordering()
    {
        const string second = "lessons/pending/10/def/Second.pdf";
        await _blobs.UploadAsync(second, [3]);
        int id;
        using (var db = Context(10, Role.Teacher))
        {
            // Keep Old.docx, add a second file in front: order of kept files unchanged, only an addition.
            id = (await Service(db, 10, Role.Teacher).ProposeAsync(
                1, LessonChangeType.Edit, Edit(("Second.pdf", second), ("Old.docx", LiveKey)))).Id;
        }

        using var approverDb = Context(20, Role.Approver);
        var review = await Service(approverDb, 20, Role.Approver).GetReviewAsync(id);

        Assert.Equal([ReviewFileChange.Added, ReviewFileChange.Unchanged], review.ProposedFiles.Select(f => f.Change));
        Assert.False(review.FileOrderChanged);
    }

    [Fact]
    public async Task Review_IsApproverOnly()
    {
        var id = await ProposeAsync(("Old.docx", LiveKey));

        using var db = Context(10, Role.Teacher);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(db, 10, Role.Teacher).GetReviewAsync(id));
    }
}
