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
/// Same SQLite-in-memory-via-DI pattern as <see cref="LessonProposalServiceTests"/>.
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
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", ContentBlobKey = "blob/l1", IsActive = true });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "First Aid", ContentBlobKey = "blob/l2", IsActive = true });
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
            TransportOffsiteConsent = true,
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.MarkCompleteAsync(studentId: 100, lessonId: 1, note: null, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.StudentLessonCompletions.Where(c => c.StudentId == 100));
    }

    [Fact]
    public async Task MarkCompleteAsync_WithNoConsentRecord_Throws()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
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
}
