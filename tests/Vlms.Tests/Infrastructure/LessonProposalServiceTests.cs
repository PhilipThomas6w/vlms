using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the curriculum-management workflow (docs/design/low-level-design.md
/// "LessonProposalService", docs/requirements/functional.md "Curriculum management"): propose →
/// approve (applies to Lesson) / reject-with-comments → resubmit, and that only the Approver role
/// can decide — enforced by the service itself, not just assumed from UI gating.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="SensitiveDataAccessControlTests"/>: real
/// <see cref="VlmsDbContext"/> resolved from a small DI container per simulated caller, named
/// shared-cache in-memory database kept alive by one anchor connection.
/// </summary>
public sealed class LessonProposalServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public LessonProposalServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-proposal-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();

        SeedReferenceData(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ICurrentUserContext CurrentUser { get; } = currentUser;

        public LessonProposalService Service => new(Context, CurrentUser);

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(int? userId, params Role[] roles)
    {
        var currentUser = new FakeCurrentUserContext(userId, roles);

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context, currentUser);
    }

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 11, DisplayName = "Teacher Two", Email = "teacher2@example.com" });
        context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Approver One", Email = "approver1@example.com" });
        context.Lessons.Add(new Lesson
        {
            Id = 1,
            RankId = 1,
            Code = "L1",
            Title = "Knots 101",
            Order = 1,
            IsActive = true
        });
        context.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = 1, Tag = LessonTag.ChristianLife });
        context.LessonFiles.Add(new LessonFile { Id = 1, LessonId = 1, Order = 1, FileName = "Knots.docx", BlobKey = "blob/knots-101-v1" });
        context.SaveChanges();
    }

    private static ProposedLessonContent NewLesson(string code = "L2", string title = "First Aid Basics", bool isActive = true) => new()
    {
        RankId = 1,
        Code = code,
        Title = title,
        Order = 2,
        Tags = [LessonTag.ChurchLife],
        Files = [new ProposedLessonFile { FileName = $"{code}.docx", BlobKey = $"lessons/pending/10/{code}/{code}.docx" }],
        IsActive = isActive
    };

    private static ProposedLessonContent EditedLesson(bool isActive = true) => new()
    {
        Code = "L1",
        Title = "Knots 101 (revised)",
        Order = 3,
        Tags = [LessonTag.Prayers, LessonTag.Saints],
        Files =
        [
            new ProposedLessonFile { FileName = "Knots v2.docx", BlobKey = "lessons/pending/10/v2/Knots-v2.docx" },
            new ProposedLessonFile { FileName = "Handout.pdf", BlobKey = "lessons/pending/10/v2/Handout.pdf" }
        ],
        IsActive = isActive
    };

    [Fact]
    public async Task ProposeAsync_ByTeacher_CreatesPendingProposal()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        var proposal = await sut.ProposeAsync(lessonId: null, LessonChangeType.Create, NewLesson(), CancellationToken.None);

        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(10, proposal.ProposedByUserId);
        Assert.Null(proposal.LessonId);
        Assert.Null(proposal.ApproverUserId);
        Assert.Null(proposal.DecidedAt);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.LessonChangeProposals.Where(p => p.Id == proposal.Id));
    }

    [Fact]
    public async Task ProposeAsync_ByNonTeacher_Throws()
    {
        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.ProposeAsync(lessonId: null, LessonChangeType.Create, NewLesson(), CancellationToken.None));
    }

    [Fact]
    public async Task ApproveAsync_CreateProposal_CreatesLesson_AndApprovesProposal()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var created = await proposeSut.ProposeAsync(
                lessonId: null, LessonChangeType.Create, NewLesson(code: "L9", title: "Map Reading"), CancellationToken.None);
            proposalId = created.Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            var approveSut = approver.Service;
            var approved = await approveSut.ApproveAsync(proposalId, CancellationToken.None);

            Assert.Equal(ProposalStatus.Approved, approved.Status);
            Assert.Equal(20, approved.ApproverUserId);
            Assert.NotNull(approved.DecidedAt);
            Assert.NotNull(approved.LessonId);
        }

        using var verify = CreateContext(1, Role.Admin);
        var lesson = Assert.Single(verify.Context.Lessons.Where(l => l.Code == "L9"));
        Assert.Equal("Map Reading", lesson.Title);
        Assert.Equal(2, lesson.Order);
        Assert.True(lesson.IsActive);
        Assert.Equal([LessonTag.ChurchLife], verify.Context.LessonTagAssignments.Where(t => t.LessonId == lesson.Id).Select(t => t.Tag).ToList());
        var file = Assert.Single(verify.Context.LessonFiles.Where(f => f.LessonId == lesson.Id));
        Assert.Equal(("L9.docx", "lessons/pending/10/L9/L9.docx", 1), (file.FileName, file.BlobKey, file.Order));

        var proposal = await verify.Context.LessonChangeProposals.SingleAsync(p => p.Id == proposalId);
        Assert.Equal(lesson.Id, proposal.LessonId);
    }

    [Fact]
    public async Task ApproveAsync_EditProposal_UpdatesExistingLesson()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            var approveSut = approver.Service;
            await approveSut.ApproveAsync(proposalId, CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        var lesson = await verify.Context.Lessons.SingleAsync(l => l.Id == 1);
        Assert.Equal("Knots 101 (revised)", lesson.Title);
        Assert.Equal(3, lesson.Order);
        Assert.True(lesson.IsActive);

        var tags = verify.Context.LessonTagAssignments.Where(t => t.LessonId == 1).Select(t => t.Tag).OrderBy(t => t).ToList();
        Assert.Equal([LessonTag.Prayers, LessonTag.Saints], tags);

        var files = verify.Context.LessonFiles.Where(f => f.LessonId == 1).OrderBy(f => f.Order)
            .Select(f => new { f.Order, f.FileName, f.BlobKey }).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal((1, "Knots v2.docx", "lessons/pending/10/v2/Knots-v2.docx"), (files[0].Order, files[0].FileName, files[0].BlobKey));
        Assert.Equal((2, "Handout.pdf", "lessons/pending/10/v2/Handout.pdf"), (files[1].Order, files[1].FileName, files[1].BlobKey));
    }

    [Fact]
    public async Task ProposeAsync_WithNoTags_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var content = new ProposedLessonContent
        {
            RankId = 1, Code = "L5", Title = "Untagged", Order = 1, Tags = [], Files = [], IsActive = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.ProposeAsync(lessonId: null, LessonChangeType.Create, content, CancellationToken.None));
        Assert.Contains("tag", ex.Message);
    }

    [Fact]
    public async Task ProposeAsync_WithOrderBelowOne_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var content = new ProposedLessonContent
        {
            RankId = 1, Code = "L5", Title = "Bad order", Order = 0, Tags = [LessonTag.Saints], Files = [], IsActive = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.ProposeAsync(lessonId: null, LessonChangeType.Create, content, CancellationToken.None));
    }

    [Fact]
    public async Task ProposeAsync_WithBlankFileLocation_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var content = new ProposedLessonContent
        {
            RankId = 1, Code = "L5", Title = "Blank file", Order = 1, Tags = [LessonTag.Saints],
            Files = [new ProposedLessonFile { FileName = "x.docx", BlobKey = " " }], IsActive = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.ProposeAsync(lessonId: null, LessonChangeType.Create, content, CancellationToken.None));
    }

    [Fact]
    public async Task ApproveAsync_RetireProposal_SetsLessonInactive()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(
                lessonId: 1, LessonChangeType.Retire, EditedLesson(isActive: false), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            var approveSut = approver.Service;
            await approveSut.ApproveAsync(proposalId, CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        var lesson = await verify.Context.Lessons.SingleAsync(l => l.Id == 1);
        Assert.False(lesson.IsActive);
    }

    [Fact]
    public async Task ApproveAsync_ByTeacher_WhoIsNotApprover_Throws_AndLeavesProposalAndLessonUnchanged()
    {
        // The core enforcement claim: only the Approver role can approve — not just a matter of
        // which page routes a caller here. A Teacher (even the proposer themselves) must be denied.
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var created = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = created.Id;
        }

        using (var sameTeacher = CreateContext(10, Role.Teacher))
        {
            var sut = sameTeacher.Service;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.ApproveAsync(proposalId, CancellationToken.None));
        }

        using var verify = CreateContext(1, Role.Admin);
        var proposal = await verify.Context.LessonChangeProposals.SingleAsync(p => p.Id == proposalId);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);

        var lesson = await verify.Context.Lessons.SingleAsync(l => l.Id == 1);
        Assert.Equal("Knots 101", lesson.Title);
    }

    [Fact]
    public async Task RejectAsync_ByApprover_SetsRejectedStatus_AndStoresComments()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            var sut = approver.Service;
            var rejected = await sut.RejectAsync(proposalId, "Needs a clearer safety warning.", CancellationToken.None);

            Assert.Equal(ProposalStatus.Rejected, rejected.Status);
            Assert.Equal("Needs a clearer safety warning.", rejected.ApprovalComments);
            Assert.Equal(20, rejected.ApproverUserId);
            Assert.NotNull(rejected.DecidedAt);
        }

        using var verify = CreateContext(1, Role.Admin);
        var lesson = await verify.Context.Lessons.SingleAsync(l => l.Id == 1);
        Assert.Equal("Knots 101", lesson.Title); // untouched by rejection
    }

    [Fact]
    public async Task RejectAsync_ByTeacher_Throws()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using var sameTeacher = CreateContext(10, Role.Teacher);
        var sut = sameTeacher.Service;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RejectAsync(proposalId, "No.", CancellationToken.None));
    }

    [Fact]
    public async Task ResubmitAsync_AfterRejection_CreatesNewProposal_ChainedToOriginal()
    {
        int originalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            originalId = proposal.Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            var rejectSut = approver.Service;
            await rejectSut.RejectAsync(originalId, "Add more detail.", CancellationToken.None);
        }

        // Resubmitted by a *different* Teacher — allowed, "same or a different Teacher".
        int resubmissionId;
        using (var otherTeacher = CreateContext(11, Role.Teacher))
        {
            var resubmitSut = otherTeacher.Service;
            var created = await resubmitSut.ResubmitAsync(originalId, EditedLesson(), CancellationToken.None);

            Assert.Equal(ProposalStatus.Pending, created.Status);
            Assert.Equal(11, created.ProposedByUserId);
            Assert.Equal(originalId, created.ResubmissionOfProposalId);
            resubmissionId = created.Id;
        }

        using var verify = CreateContext(1, Role.Admin);
        var original = await verify.Context.LessonChangeProposals.SingleAsync(p => p.Id == originalId);
        Assert.Equal(ProposalStatus.Rejected, original.Status);
        Assert.Equal("Add more detail.", original.ApprovalComments);

        var resubmission = await verify.Context.LessonChangeProposals.SingleAsync(p => p.Id == resubmissionId);
        Assert.Equal(originalId, resubmission.ResubmissionOfProposalId);
        Assert.NotEqual(originalId, resubmission.Id);
    }

    [Fact]
    public async Task ResubmitAsync_WithNoTags_Throws()
    {
        int originalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            originalId = (await teacher.Service.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None)).Id;
        }

        using (var approver = CreateContext(20, Role.Approver))
        {
            await approver.Service.RejectAsync(originalId, "Retag it.", CancellationToken.None);
        }

        using var resubmitter = CreateContext(10, Role.Teacher);
        var untagged = new ProposedLessonContent
        {
            Code = "L1", Title = "Knots 101", Order = 1, Tags = [], Files = [], IsActive = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resubmitter.Service.ResubmitAsync(originalId, untagged, CancellationToken.None));
    }

    [Fact]
    public async Task ResubmitAsync_WhenOriginalIsNotRejected_Throws()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using var teacher2 = CreateContext(10, Role.Teacher);
        var sut = teacher2.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResubmitAsync(proposalId, EditedLesson(), CancellationToken.None));
    }

    [Fact]
    public async Task ApproveAsync_ProposalAlreadyDecided_Throws()
    {
        int proposalId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var proposeSut = teacher.Service;
            var proposal = await proposeSut.ProposeAsync(lessonId: 1, LessonChangeType.Edit, EditedLesson(), CancellationToken.None);
            proposalId = proposal.Id;
        }

        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;
        await sut.ApproveAsync(proposalId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ApproveAsync(proposalId, CancellationToken.None));
    }
}
