using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Committee referral and Approver alerts (FR-007, docs/adr/0013 decision 8), each rule enforced by
/// <see cref="LessonSuggestionService"/> itself: only Approvers refer or approve; only a submitted, unreferred, current
/// suggestion can be referred; each Approver approves a referred suggestion once; it is published exactly on the third
/// different approval, the author's own counting; a send-back ends it; two approvals at the same moment cannot both take
/// one place; the referral, approvals and outcome are in the history; every Approver is emailed on submission; and the
/// menu count is what awaits the caller. Real <see cref="VlmsDbContext"/> on a shared-cache SQLite in-memory database.
/// </summary>
public sealed class LessonSuggestionCommitteeTests : IDisposable
{
    private const int Admin = 1, Teacher = 10, ApproverA = 20, ApproverB = 21, ApproverC = 22, ApproverD = 23, LeftApprover = 24;
    private const int LessonId = 1;
    private const string LessonCode = "R1-01";
    private const string OriginalBody = "## Questions\n\n- Why are these two saints so well known?\n";
    private const string ChangedBody = OriginalBody + "- Which two Liturgies are named after them?\n";

    private static readonly Role[] TeacherRoles = [Role.Teacher];
    private static readonly Role[] ApproverRoles = [Role.Approver];

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();
    private readonly FakeNotificationService _emails = new();

    public LessonSuggestionCommitteeTests()
    {
        _connectionString = $"Data Source=file:vlms-committee-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "RECRUIT", Name = "Recruit" });
        foreach (var (id, name) in new[] { (Admin, "Admin One"), (Teacher, "Teacher One"), (ApproverA, "Approver A"),
                     (ApproverB, "Approver B"), (ApproverC, "Approver C"), (ApproverD, "Approver D") })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com" });
        }
        db.AppUsers.Add(new AppUser { Id = LeftApprover, DisplayName = "Approver Left", Email = "left@example.com", HasLeft = true, LeftAt = DateTime.UtcNow });
        foreach (var id in new[] { ApproverA, ApproverB, ApproverC, ApproverD, LeftApprover })
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = Role.Approver });
        }
        db.UserRoles.Add(new UserRole { UserId = Teacher, Role = Role.Teacher, ChapterId = Chapter.ShrewsburyId });
        db.Lessons.Add(new Lesson
        {
            Id = LessonId, RankId = 1, Code = LessonCode, Title = "St John Chrysostom and St Basil", Order = 1, IsActive = true,
            Body = OriginalBody, ContentVersion = "v0"
        });
        db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = LessonId, Tag = LessonTag.Saints });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private VlmsDbContext Context(ICurrentUserContext user, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }
        return new VlmsDbContext(options.Options, user);
    }

    private async Task<T> As<T>(int userId, Role[] roles, Func<LessonSuggestionService, Task<T>> action, IInterceptor? interceptor = null)
    {
        var user = new FakeCurrentUserContext(userId, roles);
        using var db = Context(user, interceptor);
        var notifications = new LessonSuggestionNotificationService(db, _emails, NullLogger<LessonSuggestionNotificationService>.Instance,
            "https://vlms.example");
        return await action(new LessonSuggestionService(db, user, _blobs, notifications));
    }

    private Task As(int userId, Role[] roles, Func<LessonSuggestionService, Task> action) =>
        As(userId, roles, async s => { await action(s); return 0; });

    private static SuggestionInput Edit(string body = ChangedBody) =>
        new(null, LessonId, 1, LessonChangeKind.Edit, "St John Chrysostom and St Basil", 1, [LessonTag.Saints], body, "Add a question.");

    private async Task<int> SubmitAndReferAsync(int author = Teacher, Role[]? authorRoles = null)
    {
        var id = await As(author, authorRoles ?? TeacherRoles, s => s.SubmitAsync(Edit()));
        await As(ApproverA, ApproverRoles, s => s.ReferAsync(id, "A major change."));
        return id;
    }

    // --- Roles -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.NationalAdministrator)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    public async Task OnlyApprovers_MayReferOrGiveACommitteeApproval(Role role)
    {
        var submitted = await As(Teacher, TeacherRoles, s => s.SubmitAsync(Edit()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Teacher, [role], s => s.ReferAsync(submitted, null)));

        await As(ApproverA, ApproverRoles, s => s.ReferAsync(submitted, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Teacher, [role], s => s.ApproveReferredAsync(submitted)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Teacher, [role], s => s.SendBackAsync(submitted, "No.")));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.False(await db.LessonSuggestionApprovals.AnyAsync());
        Assert.Equal(LessonSuggestionStatus.Submitted, (await db.LessonSuggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task ATeachersChange_AlwaysGoesToReview()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => As(Teacher, TeacherRoles, s => s.PublishNowAsync(Edit())));
        Assert.False((await As(Teacher, TeacherRoles, s => s.GetEditorAsync(LessonCode)))!.CanPublishNow);

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.False(await db.LessonVersions.AnyAsync(v => v.Number > 1));
    }

    // --- Referring -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Referring_RecordsWhoWhenAndWhy_AndKeepsItAwaitingReview()
    {
        var id = await SubmitAndReferAsync();

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        var suggestion = await db.LessonSuggestions.SingleAsync(s => s.Id == id);
        Assert.Equal((LessonSuggestionStatus.Submitted, (int?)ApproverA, "A major change."),
            (suggestion.Status, suggestion.ReferredByUserId, suggestion.ReferralReason));
        Assert.NotNull(suggestion.ReferredAt);

        var review = await As(ApproverB, ApproverRoles, s => s.GetReviewAsync(id));
        Assert.Equal(("Approver A", "A major change."), (review!.Referral!.ReferredBy, review.Referral.Reason));
        Assert.Empty(review.CommitteeApprovals!);
    }

    [Fact]
    public async Task OnlyASubmittedCurrentSuggestion_NotAlreadyReferred_CanBeReferred()
    {
        var draft = await As(Teacher, TeacherRoles, s => s.SaveDraftAsync(Edit()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ReferAsync(draft, null)));
        Assert.Equal("This suggestion is not awaiting review.", ex.Message);
        await As(Teacher, TeacherRoles, s => s.DiscardAsync(draft));

        // Approved, and sent back.
        var approved = await As(Teacher, TeacherRoles, s => s.SubmitAsync(Edit()));
        await As(ApproverA, ApproverRoles, s => s.ApproveAsync(approved));
        await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ReferAsync(approved, null)));
        var sentBack = await As(Teacher, TeacherRoles, s => s.SubmitAsync(Edit(ChangedBody + "- More?\n")));
        await As(ApproverA, ApproverRoles, s => s.SendBackAsync(sentBack, "Not yet."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ReferAsync(sentBack, null)));

        // Already referred.
        var referred = await SubmitAndReferAsync();
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverB, ApproverRoles, s => s.ReferAsync(referred, null)));
        Assert.Equal("This suggestion has already been referred to committee.", ex.Message);
        await As(ApproverA, ApproverRoles, s => s.SendBackAsync(referred, "Start again."));

        // Based on an older version of the lesson.
        var stale = await As(Teacher, TeacherRoles, s => s.SubmitAsync(Edit(OriginalBody + "- Stale?\n")));
        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            var number = await db.LessonVersions.Where(v => v.LessonId == LessonId).MaxAsync(v => v.Number) + 1;
            db.LessonVersions.Add(new LessonVersion
            {
                Id = await db.NextIdAsync<LessonVersion>(), LessonId = LessonId, Number = number, Kind = LessonVersionKind.Converted,
                Title = "St John Chrysostom and St Basil", Order = 1, IsActive = true, Body = "Newer.\n", PublishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ReferAsync(stale, null)));
        Assert.Contains("has changed since this suggestion was started", ex.Message);
    }

    [Fact]
    public async Task AReferredSuggestion_CannotBeApprovedByOneApproverAlone()
    {
        var id = await SubmitAndReferAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ApproveAsync(id)));
        Assert.Contains("referred to committee", ex.Message);

        var unreferred = await As(Teacher, TeacherRoles, s => s.SubmitAsync(new SuggestionInput(null, null, 1, LessonChangeKind.NewLesson,
            "The Sign of the Cross", 2, [LessonTag.Prayers], "## How we cross ourselves\n", "New lesson.")));
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(unreferred)));
        Assert.Equal("This suggestion has not been referred to committee.", ex.Message);
    }

    // --- Committee approvals ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AReferredSuggestion_IsPublishedExactlyOnTheThirdDifferentApproval()
    {
        var id = await SubmitAndReferAsync();

        var first = await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id));
        var second = await As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id));
        Assert.Equal((1, (PublishedVersion?)null), (first.Approvals, first.Published));
        Assert.Equal((2, (PublishedVersion?)null), (second.Approvals, second.Published));
        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            Assert.Equal(OriginalBody, (await db.Lessons.SingleAsync()).Body);
            Assert.Equal(LessonSuggestionStatus.Submitted, (await db.LessonSuggestions.SingleAsync(s => s.Id == id)).Status);
        }

        var third = await As(ApproverC, ApproverRoles, s => s.ApproveReferredAsync(id));

        Assert.Equal(3, third.Approvals);
        Assert.Equal((2, LessonVersionKind.ApprovedSuggestion), (third.Published!.Number, third.Published.Kind));
        using var after = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.Equal(ChangedBody, (await after.Lessons.SingleAsync()).Body);
        var version = await after.LessonVersions.SingleAsync(v => v.Number == 2);
        Assert.Equal(((int?)Teacher, (int?)ApproverC, (int?)id), (version.SuggestedByUserId, version.ApprovedByUserId, version.SuggestionId));
        Assert.Equal(LessonSuggestionStatus.Approved, (await after.LessonSuggestions.SingleAsync(s => s.Id == id)).Status);

        // Nothing more can be approved once it is published.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverD, ApproverRoles, s => s.ApproveReferredAsync(id)));
        Assert.Equal("This suggestion is not awaiting review.", ex.Message);
    }

    [Fact]
    public async Task AnApprover_ApprovesAReferredSuggestionOnlyOnce()
    {
        var id = await SubmitAndReferAsync();
        await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id)));
        Assert.Equal("You have already approved this suggestion.", ex.Message);
        await As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id)));

        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        Assert.Equal(2, await db.LessonSuggestionApprovals.CountAsync());
        Assert.Equal(OriginalBody, (await db.Lessons.SingleAsync()).Body);
        Assert.True((await As(ApproverA, ApproverRoles, s => s.GetReviewAsync(id)))!.ApprovedByMe);
        Assert.False((await As(ApproverC, ApproverRoles, s => s.GetReviewAsync(id)))!.ApprovedByMe);
    }

    [Fact]
    public async Task TheDatabase_AllowsOneApprovalPerApproverAndPerPlace()
    {
        var id = await SubmitAndReferAsync();
        using var db = Context(new FakeCurrentUserContext(Admin, Role.Admin));
        db.LessonSuggestionApprovals.Add(new LessonSuggestionApproval { Id = 1, LessonSuggestionId = id, ApproverUserId = ApproverA, Position = 1, ApprovedAt = DateTime.UtcNow });
        db.SaveChanges();

        db.LessonSuggestionApprovals.Add(new LessonSuggestionApproval { Id = 2, LessonSuggestionId = id, ApproverUserId = ApproverA, Position = 2, ApprovedAt = DateTime.UtcNow });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        db.LessonSuggestionApprovals.Add(new LessonSuggestionApproval { Id = 3, LessonSuggestionId = id, ApproverUserId = ApproverB, Position = 1, ApprovedAt = DateTime.UtcNow });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task TheAuthorsOwnApproval_CountsTowardsTheThree()
    {
        var id = await SubmitAndReferAsync(author: ApproverD, authorRoles: ApproverRoles);

        await As(ApproverD, ApproverRoles, s => s.ApproveReferredAsync(id));
        await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id));
        var third = await As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id));

        Assert.Equal(2, third.Published!.Number);
    }

    [Fact]
    public async Task TwoApprovalsAtTheSameMoment_CannotBothTakeOnePlace()
    {
        var id = await SubmitAndReferAsync();
        await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id));

        // Approver C's approval lands between Approver B reading the approvals and saving theirs.
        var racer = new BeforeSave(async () =>
        {
            using var other = Context(new FakeCurrentUserContext(ApproverC, Role.Approver));
            other.LessonSuggestionApprovals.Add(new LessonSuggestionApproval
            {
                Id = await other.NextIdAsync<LessonSuggestionApproval>(), LessonSuggestionId = id, ApproverUserId = ApproverC, Position = 2,
                ApprovedAt = DateTime.UtcNow
            });
            await other.SaveChangesAsync();
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id), racer));
        Assert.Contains("at the same moment", ex.Message);

        using (var db = Context(new FakeCurrentUserContext(Admin, Role.Admin)))
        {
            Assert.Equal([ApproverA, ApproverC], await db.LessonSuggestionApprovals.OrderBy(a => a.Position).Select(a => a.ApproverUserId).ToListAsync());
            Assert.Equal(OriginalBody, (await db.Lessons.SingleAsync()).Body);
        }

        // Trying again, Approver B's is the third, and publishes it.
        var retry = await As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id));
        Assert.Equal((3, 2), (retry.Approvals, retry.Published!.Number));
    }

    // --- Sending back ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendingBackAReferredSuggestion_NeedsAComment_EndsIt_AndKeepsTheRecord()
    {
        var id = await SubmitAndReferAsync();
        await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(id));
        await As(ApproverB, ApproverRoles, s => s.ApproveReferredAsync(id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverC, ApproverRoles, s => s.SendBackAsync(id, " ")));
        await As(ApproverC, ApproverRoles, s => s.SendBackAsync(id, "The second Liturgy is St Basil's."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => As(ApproverD, ApproverRoles, s => s.ApproveReferredAsync(id)));
        Assert.Equal("This suggestion is not awaiting review.", ex.Message);

        var history = await As(ApproverA, ApproverRoles, s => s.GetHistoryAsync(LessonCode));
        Assert.Single(history!.Versions);
        var sentBack = Assert.Single(history.SentBack);
        Assert.Equal(("Approver C", "The second Liturgy is St Basil's."), (sentBack.SentBackBy, sentBack.Comment));
        Assert.Equal(("Approver A", "A major change."), (sentBack.Referral!.ReferredBy, sentBack.Referral.Reason));
        Assert.Equal(["Approver A", "Approver B"], sentBack.CommitteeApprovals!.Select(a => a.ApprovedBy));

        // It returns to its author as a draft to carry on, as any suggestion sent back does.
        var editor = await As(Teacher, TeacherRoles, s => s.GetEditorAsync(LessonCode));
        Assert.Equal((null, id), (editor!.Draft!.Id, editor.Draft.RevisesSuggestionId));
    }

    // --- History ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHistory_ShowsTheReferralAndEachCommitteeApprover()
    {
        var id = await SubmitAndReferAsync();
        foreach (var approver in new[] { ApproverB, ApproverC, ApproverA })
        {
            await As(approver, ApproverRoles, s => s.ApproveReferredAsync(id));
        }

        var history = await As(Admin, [Role.Admin], s => s.GetHistoryAsync(LessonCode));

        var latest = history!.Versions[0];
        Assert.Equal((2, "Approver A", "A major change."), (latest.Number, latest.Referral!.ReferredBy, latest.Referral.Reason));
        Assert.Equal(["Approver B", "Approver C", "Approver A"], latest.CommitteeApprovals!.Select(a => a.ApprovedBy));
        Assert.Null(history.Versions[1].Referral);
    }

    // --- Alerts ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Submitting_EmailsEveryApproverWhoHasNotLeft()
    {
        await As(Teacher, TeacherRoles, s => s.SaveDraftAsync(Edit()));
        Assert.Empty(_emails.SentRequests);

        var id = await As(Teacher, TeacherRoles, s => s.SubmitAsync(Edit() with { SuggestionId = 1 }));

        Assert.Equal(["user20@example.com", "user21@example.com", "user22@example.com", "user23@example.com"],
            _emails.SentRequests.Select(r => r.RecipientEmail));
        var email = _emails.SentRequests[0];
        Assert.Equal("VLMS: change to review (St John Chrysostom and St Basil)", email.Subject);
        Assert.Contains("Teacher One has suggested a change", email.Body);
        Assert.Contains($"https://vlms.example/curriculum/review/{id}", email.Body);
        Assert.DoesNotContain("Liturgies", email.Body);
    }

    [Fact]
    public async Task TheMenuCount_IsWhatAwaitsTheCaller()
    {
        var referred = await SubmitAndReferAsync();
        await As(Teacher, TeacherRoles, s => s.SubmitAsync(new SuggestionInput(null, null, 1, LessonChangeKind.NewLesson,
            "The Sign of the Cross", 2, [LessonTag.Prayers], "## How we cross ourselves\n", "New lesson.")));
        await As(Teacher, TeacherRoles, s => s.SaveDraftAsync(new SuggestionInput(null, null, 1, LessonChangeKind.NewLesson,
            "A draft", 3, [LessonTag.Prayers], "## Not submitted\n", null)));

        Assert.Equal(2, await As(ApproverA, ApproverRoles, s => s.CountAwaitingMyReviewAsync()));
        await As(ApproverA, ApproverRoles, s => s.ApproveReferredAsync(referred));
        Assert.Equal(1, await As(ApproverA, ApproverRoles, s => s.CountAwaitingMyReviewAsync()));
        Assert.Equal(2, await As(ApproverB, ApproverRoles, s => s.CountAwaitingMyReviewAsync()));
        Assert.Equal(0, await As(Teacher, TeacherRoles, s => s.CountAwaitingMyReviewAsync()));
        Assert.Equal(0, await As(Admin, [Role.Admin], s => s.CountAwaitingMyReviewAsync()));

        var queue = await As(ApproverA, ApproverRoles, s => s.GetQueueAsync());
        var row = Assert.Single(queue, q => q.Id == referred);
        Assert.Equal((true, 1, true), (row.IsReferred, row.CommitteeApprovals, row.ApprovedByMe));
    }

    // Runs an action once, just before the context saves.
    private sealed class BeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }
            return result;
        }
    }
}
