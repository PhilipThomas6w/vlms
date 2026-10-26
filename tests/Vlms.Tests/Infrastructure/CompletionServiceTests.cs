using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Progress;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies progress tracking (docs/design/low-level-design.md "CompletionService", STATE.md):
/// a Teacher records a lesson completion, blocked by expired/missing consent (functional.md
/// FR-003), and on success triggers auto-promotion + certificate generation.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LessonSuggestionServiceTests"/>.
/// </summary>
public sealed class CompletionServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobStorage = new();

    public CompletionServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-completion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();

        SeedReferenceData(schema.Context);
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(
        ServiceProvider provider, IServiceScope scope, VlmsDbContext context, ICurrentUserContext currentUser, InMemoryBlobStorage blobStorage) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ICurrentUserContext CurrentUser { get; } = currentUser;

        public CompletionService Service =>
            new(Context, CurrentUser, new PromotionService(Context), new CertificateService(Context, blobStorage));

        public CompletionService ServiceWithReversalWindow(int reversalWindowDays) =>
            new(Context, CurrentUser, new PromotionService(Context), new CertificateService(Context, blobStorage), reversalWindowDays);

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

        return new CallerContext(provider, scope, context, currentUser, _blobStorage);
    }

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Explorer" });
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", Order = 1, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "First Aid", Order = 1, IsActive = true });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "jane@example.com", IsPrimary = true });
        context.Students.Add(new Student
        {
            Id = 100,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = 1,
            StudentId = 100,
            RankId = 1,
            StartedAt = DateTime.UtcNow.AddDays(-30),
            CompletedAt = null
        });
        context.SaveChanges();
    }

    private static void AddConsent(VlmsDbContext context, int id, int studentId, ConsentStatus status, DateOnly expiryDate)
    {
        context.ConsentRecords.Add(new ConsentRecord
        {
            Id = id,
            StudentId = studentId,
            PeriodStart = new DateOnly(2026, 1, 1),
            PeriodEnd = new DateOnly(2026, 12, 31),
            PhotoMediaConsent = true,
            TransportConsent = true, OffsiteTripConsent = true,
            DataSharingConsent = true,
            Status = status,
            SubmittedByParentId = 1,
            ExpiryDate = expiryDate
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task MarkCompleteAsync_WithActiveConsent_RecordsCompletion_AndGeneratesCertificate()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 1, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        var completion = await sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: "Did well", CancellationToken.None);

        Assert.Equal(100, completion.StudentId);
        Assert.Equal(1, completion.LessonId);
        Assert.Equal(10, completion.CompletedByUserId);
        Assert.False(completion.IsReversed);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 100 && c.LessonId == 1));

        var certificate = await verify.Context.Certificates.SingleAsync(c => c.StudentLessonCompletionId == completion.Id);
        Assert.True(_blobStorage.TryGetBlob(certificate.BlobKey, out var pdfBytes));
        Assert.True(pdfBytes.Length > 0);
    }

    [Fact]
    public async Task MarkCompleteAsync_WithExpiredConsent_Throws_AndRecordsNoCompletion()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 2, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        await Assert.ThrowsAsync<ConsentBlockedException>(
            () => sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 100));
    }

    [Fact]
    public async Task MarkCompleteAsync_WithNoConsentRecord_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        await Assert.ThrowsAsync<ConsentBlockedException>(
            () => sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None));
    }

    [Fact]
    public async Task MarkCompleteAsync_ByNonTeacher_Throws()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 3, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None));
    }

    [Fact]
    public async Task MarkCompleteAsync_CompletingFinalLessonInRank_TriggersAutoPromotion()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 4, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var sut = teacher.Service;
            await sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            await sut.MarkCompleteAsync(studentId: 100, lessonId: 2, note: null, CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        var student = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(2, student.CurrentRankId);
    }

    // --- ReverseAsync (STATE.md "Teacher-facing reverse a completion flow", functional.md: "can
    // self-correct/reverse their own entry (e.g. within some reasonable window)") -----------------

    [Fact]
    public async Task ReverseAsync_ByRecordingTeacher_WithinWindow_SetsIsReversedAndReversedAt()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 10, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: "Did well", CancellationToken.None);
            completionId = completion.Id;
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var reversed = await teacher.Service.ReverseAsync(completionId, note: "Wrong student ticked", CancellationToken.None);
            Assert.True(reversed.IsReversed);
            Assert.NotNull(reversed.ReversedAt);
        }

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.True(stored.IsReversed);
        Assert.NotNull(stored.ReversedAt);
        // Reversal note is appended to the existing completion note, not overwritten (see
        // ReverseAsync's doc comment — data-design.md names only one Note field).
        Assert.Equal("Did well\nReversed: Wrong student ticked", stored.Note);
    }

    [Fact]
    public async Task ReverseAsync_WithNoExistingNote_ReversalNoteBecomesTheWholeNote()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 11, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            completionId = completion.Id;
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await teacher.Service.ReverseAsync(completionId, note: "Marked in error", CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.Equal("Reversed: Marked in error", stored.Note);
    }

    [Fact]
    public async Task ReverseAsync_WithNoReversalNote_LeavesExistingNoteUnchanged()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 12, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: "Original note", CancellationToken.None);
            completionId = completion.Id;
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await teacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None);
        }

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.True(stored.IsReversed);
        Assert.Equal("Original note", stored.Note);
    }

    [Fact]
    public async Task ReverseAsync_ByDifferentTeacher_Throws_AndRecordsNoMutation()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 13, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
            setup.Context.AppUsers.Add(new AppUser { Id = 11, DisplayName = "Teacher Two", Email = "teacher2@example.com" });
            setup.Context.SaveChanges();
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            completionId = completion.Id;
        }

        using (var otherTeacher = CreateContext(11, Role.Teacher))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => otherTeacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None));
        }

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.False(stored.IsReversed);
        Assert.Null(stored.ReversedAt);
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Student)]
    public async Task ReverseAsync_ByNonTeacher_Throws_AndRecordsNoMutation(Role role)
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 14, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            completionId = completion.Id;
        }

        using (var caller = CreateContext(99, role))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => caller.Service.ReverseAsync(completionId, note: null, CancellationToken.None));
        }

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.False(stored.IsReversed);
        Assert.Null(stored.ReversedAt);
    }

    [Fact]
    public async Task ReverseAsync_AlreadyReversed_Throws()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 15, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int completionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var completion = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            completionId = completion.Id;
            await teacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None);
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => teacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ReverseAsync_UnknownCompletionId_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.ReverseAsync(completionId: 999, note: null, CancellationToken.None));
    }

    [Fact]
    public async Task ReverseAsync_OutsideDefaultWindow_Throws()
    {
        int completionId;
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 16, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
            completionId = 500;
            setup.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = completionId,
                StudentId = 100,
                LessonId = 1,
                CompletedByUserId = 10,
                // 8 days ago — one day past CompletionService.DefaultReversalWindowDays (7).
                CompletedAt = DateTime.UtcNow.AddDays(-8),
                IsReversed = false
            });
            setup.Context.SaveChanges();
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None));
        Assert.Contains("window has passed", ex.Message);

        using var verify = CreateContext(1, Role.Admin);
        var stored = await verify.Context.StudentLessonCompletions.SingleAsync(c => c.Id == completionId);
        Assert.False(stored.IsReversed);
    }

    [Fact]
    public async Task ReverseAsync_WithinDefaultWindow_Succeeds()
    {
        int completionId;
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 17, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
            completionId = 501;
            setup.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = completionId,
                StudentId = 100,
                LessonId = 1,
                CompletedByUserId = 10,
                // 6 days ago — still inside the default 7-day window.
                CompletedAt = DateTime.UtcNow.AddDays(-6),
                IsReversed = false
            });
            setup.Context.SaveChanges();
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var reversed = await teacher.Service.ReverseAsync(completionId, note: null, CancellationToken.None);
        Assert.True(reversed.IsReversed);
    }

    /// <summary>
    /// Proves the reversal window is genuinely configurable, not just a hardcoded number that
    /// happens to be read from a constant — the same "configurable constructor parameter actually
    /// changes behaviour" property <c>ConsentExpiryJobTests</c>/<c>NotificationServiceTests</c>
    /// already established for their own configurable defaults.
    /// </summary>
    [Fact]
    public async Task ReverseAsync_WithConfiguredShorterWindow_RejectsWhatTheDefaultWindowWouldAllow()
    {
        int completionId;
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 18, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
            completionId = 502;
            setup.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = completionId,
                StudentId = 100,
                LessonId = 1,
                CompletedByUserId = 10,
                // 2 days ago — inside the default 7-day window, but outside a configured 1-day one.
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                IsReversed = false
            });
            setup.Context.SaveChanges();
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var shortWindowService = teacher.ServiceWithReversalWindow(1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => shortWindowService.ReverseAsync(completionId, note: null, CancellationToken.None));

        var longWindowService = teacher.ServiceWithReversalWindow(30);
        var reversed = await longWindowService.ReverseAsync(completionId, note: null, CancellationToken.None);
        Assert.True(reversed.IsReversed);
    }

    [Fact]
    public void Constructor_WithNonPositiveReversalWindow_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        Assert.Throws<ArgumentOutOfRangeException>(() => teacher.ServiceWithReversalWindow(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => teacher.ServiceWithReversalWindow(-1));
    }

    /// <summary>
    /// The property that actually matters (STATE.md's own framing): proves a completion reversed
    /// through the real <see cref="CompletionService.ReverseAsync"/> write path — not test seed data
    /// setting <see cref="StudentLessonCompletion.IsReversed"/> directly, which is all
    /// <see cref="PromotionServiceTests"/> does — is genuinely excluded from
    /// <see cref="PromotionService"/>'s rank-progression counting. Sequence: complete L1, reverse it,
    /// then complete L2 — since L1's completion no longer counts, the rank isn't yet fully complete
    /// (still 1 of 2 active lessons), so no promotion happens even though both lessons have at some
    /// point had a completion recorded. Re-completing L1 afterward promotes normally.
    /// </summary>
    [Fact]
    public async Task ReverseAsync_ThenCompletingRemainingLesson_DoesNotTriggerPromotion_UntilReversedLessonIsRedone()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, id: 19, studentId: 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        int firstCompletionId;
        using (var teacher = CreateContext(10, Role.Teacher))
        {
            var first = await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None);
            firstCompletionId = first.Id;
            await teacher.Service.ReverseAsync(firstCompletionId, note: "Wrong lesson", CancellationToken.None);
            await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 2, note: null, CancellationToken.None);
        }

        using (var verify = CreateContext(1, Role.Admin))
        {
            var student = await verify.Context.Students.SingleAsync(s => s.Id == 100);
            Assert.Equal(1, student.CurrentRankId); // still Recruit — L1's only completion is reversed
        }

        using (var teacher = CreateContext(10, Role.Teacher))
        {
            await teacher.Service.MarkCompleteAsync(studentId: 100, lessonId: 1, note: "Redone correctly", CancellationToken.None);
        }

        using var finalVerify = CreateContext(1, Role.Admin);
        var promotedStudent = await finalVerify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(2, promotedStudent.CurrentRankId); // now promoted — both active lessons genuinely complete
    }

    private static void AddStudent(VlmsDbContext context, int id)
    {
        context.Students.Add(new Student
        {
            Id = id,
            Name = $"Student {id}",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = id,
            StudentId = id,
            RankId = 1,
            StartedAt = DateTime.UtcNow.AddDays(-30),
            CompletedAt = null
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task MarkCompleteBatchAsync_MixedStudents_ReportsEachOutcome_AndOneBlockDoesNotStopOthers()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6));
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddStudent(setup.Context, 101); // lapsed consent
            AddStudent(setup.Context, 102); // already completed
            AddStudent(setup.Context, 103); // ok
            AddConsent(setup.Context, 10, 100, ConsentStatus.Approved, future);
            AddConsent(setup.Context, 11, 101, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
            AddConsent(setup.Context, 12, 102, ConsentStatus.Approved, future);
            AddConsent(setup.Context, 13, 103, ConsentStatus.Approved, future);
        }

        using var teacher = CreateContext(10, Role.Teacher);
        await teacher.Service.MarkCompleteAsync(102, 1, null, CancellationToken.None);

        var results = await teacher.Service.MarkCompleteBatchAsync(new[] { 100, 101, 102, 103, 100 }, 1, "Batch");

        Assert.Equal(4, results.Count); // duplicate id collapsed
        Assert.Equal(CompletionOutcome.Completed, results.Single(r => r.StudentId == 100).Outcome);
        var blocked = results.Single(r => r.StudentId == 101);
        Assert.Equal(CompletionOutcome.ConsentBlocked, blocked.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Message));
        Assert.Equal(CompletionOutcome.AlreadyCompleted, results.Single(r => r.StudentId == 102).Outcome);
        Assert.Equal(CompletionOutcome.Completed, results.Single(r => r.StudentId == 103).Outcome);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 102 && c.LessonId == 1));
        Assert.Empty(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 101));
        Assert.Single(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 100 && c.LessonId == 1));
        Assert.Single(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 103 && c.LessonId == 1));
    }

    [Fact]
    public async Task MarkCompleteBatchAsync_UnknownStudent_IsReportedFailed_AndOthersStillRecorded()
    {
        using (var setup = CreateContext(1, Role.Admin))
        {
            AddConsent(setup.Context, 20, 100, ConsentStatus.Approved, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
        }

        using var teacher = CreateContext(10, Role.Teacher);
        var results = await teacher.Service.MarkCompleteBatchAsync(new[] { 999, 100 }, 1);

        Assert.Equal(CompletionOutcome.Failed, results.Single(r => r.StudentId == 999).Outcome);
        Assert.Equal(CompletionOutcome.Completed, results.Single(r => r.StudentId == 100).Outcome);
    }

    [Fact]
    public async Task MarkCompleteBatchAsync_ByNonTeacher_Throws()
    {
        using var parent = CreateContext(99, Role.Parent);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => parent.Service.MarkCompleteBatchAsync(new[] { 100 }, 1));
    }

    [Fact]
    public async Task MarkCompleteBatchAsync_UnknownLesson_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => teacher.Service.MarkCompleteBatchAsync(new[] { 100 }, 999));
    }
}
