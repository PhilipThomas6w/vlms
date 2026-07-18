using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Progress;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies certificate generation (docs/design/low-level-design.md "CertificateService",
/// STATE.md): a QuestPDF-generated PDF is uploaded to blob storage (via the in-memory
/// <see cref="InMemoryBlobStorage"/> fake, not a real Azure Storage account — see
/// openwiki/testing.md) and a <see cref="Certificate"/> row is written, 1:1 with the completion
/// (data-design.md).
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LessonProposalServiceTests"/>.
/// </summary>
public sealed class CertificateServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobStorage = new();
    private const int CompletionId = 1;

    public CertificateServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-certificate-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Context.Database.EnsureCreated();

        schema.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        schema.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", ContentBlobKey = "blob/l1", IsActive = true });
        schema.Context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        schema.Context.Students.Add(new Student
        {
            Id = 100,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        schema.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = CompletionId,
            StudentId = 100,
            LessonId = 1,
            CompletedByUserId = 10,
            CompletedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            IsReversed = false
        });
        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, InMemoryBlobStorage blobStorage) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public CertificateService Service => new(Context, blobStorage);

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    // CertificateService does no role checks of its own (see its doc comment), so the caller
    // identity is irrelevant here.
    private CallerContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(1, Role.Admin));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context, _blobStorage);
    }

    [Fact]
    public async Task GenerateAsync_CreatesCertificateRecord_LinkedToCompletion()
    {
        using var caller = CreateContext();
        var certificate = await caller.Service.GenerateAsync(CompletionId, CancellationToken.None);

        Assert.Equal(CompletionId, certificate.StudentLessonCompletionId);
        Assert.False(string.IsNullOrWhiteSpace(certificate.BlobKey));

        using var verify = CreateContext();
        Assert.Single(verify.Context.Certificates.Where(c => c.StudentLessonCompletionId == CompletionId));
    }

    [Fact]
    public async Task GenerateAsync_UploadsARealPdf_ToBlobStorage()
    {
        using var caller = CreateContext();
        var certificate = await caller.Service.GenerateAsync(CompletionId, CancellationToken.None);

        Assert.True(_blobStorage.TryGetBlob(certificate.BlobKey, out var pdfBytes));
        Assert.True(pdfBytes.Length > 100); // a real generated document, not an empty placeholder

        // PDF magic bytes ("%PDF") — proves QuestPDF actually produced a PDF, not arbitrary bytes.
        var header = Encoding.ASCII.GetString(pdfBytes, 0, 4);
        Assert.Equal("%PDF", header);
    }

    [Fact]
    public async Task GenerateAsync_BlobKey_ScopedToStudentAndCompletion()
    {
        using var caller = CreateContext();
        var certificate = await caller.Service.GenerateAsync(CompletionId, CancellationToken.None);

        Assert.Contains("100", certificate.BlobKey); // StudentId
        Assert.Contains(CompletionId.ToString(), certificate.BlobKey);
    }

    [Fact]
    public async Task GenerateAsync_UnknownCompletionId_Throws()
    {
        using var caller = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => caller.Service.GenerateAsync(completionId: 999, CancellationToken.None));
    }
}
