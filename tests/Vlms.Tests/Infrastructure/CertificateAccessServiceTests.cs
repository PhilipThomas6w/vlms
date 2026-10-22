using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Progress;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies certificate view/download access (functional.md FR-010, TC-020): the owning student,
/// a linked parent who has not left, Teachers and Admins can open a certificate; an unlinked parent,
/// a parent who has left, another student, an Approver, an Admin without MFA and a caller with no
/// resolved user get null, exactly as for an unknown id.
/// </summary>
public sealed class CertificateAccessServiceTests : IDisposable
{
    private const int OwnStudentUser = 20;
    private const int OtherStudentUser = 21;
    private const int LinkedParentUser = 30;
    private const int UnlinkedParentUser = 31;
    private const int LeftParentUser = 32;
    private static readonly byte[] Pdf = [37, 80, 68, 70];

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public CertificateAccessServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-certaccess-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = CreateContext(new FakeCurrentUserContext(1, Role.Admin));
        db.Database.EnsureCreated();

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        foreach (var (id, name, left) in new[]
        {
            (1, "Teacher", false), (OwnStudentUser, "Own student", false), (OtherStudentUser, "Other student", false),
            (LinkedParentUser, "Linked parent", false), (UnlinkedParentUser, "Unlinked parent", false), (LeftParentUser, "Left parent", true)
        })
        {
            db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"u{id}@example.com", HasLeft = left, LeftAt = left ? DateTime.UtcNow : null });
        }

        db.Students.Add(NewStudent(100, OwnStudentUser));
        db.Students.Add(NewStudent(101, OtherStudentUser));
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Linked", ContactInfo = "x", IsPrimary = true, AppUserId = LinkedParentUser });
        db.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Unlinked", ContactInfo = "x", IsPrimary = false, AppUserId = UnlinkedParentUser });
        db.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Left", ContactInfo = "x", IsPrimary = false, AppUserId = LeftParentUser });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 1, CreatedByUserId = 1 });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 3, CreatedByUserId = 1 });

        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = 1, CompletedAt = DateTime.UtcNow, IsReversed = false });
        db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 2, StudentId = 100, LessonId = 1, CompletedByUserId = 1, CompletedAt = DateTime.UtcNow, IsReversed = true });
        db.Certificates.Add(new Certificate { Id = 1, StudentLessonCompletionId = 1, GeneratedAt = DateTime.UtcNow, BlobKey = "certificates/100/1.pdf" });
        db.Certificates.Add(new Certificate { Id = 2, StudentLessonCompletionId = 2, GeneratedAt = DateTime.UtcNow, BlobKey = "certificates/100/2.pdf" });
        db.SaveChanges();

        _blobs.UploadAsync("certificates/100/1.pdf", Pdf).Wait();
        _blobs.UploadAsync("certificates/100/2.pdf", Pdf).Wait();
    }

    public void Dispose() => _anchor.Dispose();

    private static Student NewStudent(int id, int appUserId) => new()
    {
        Id = id, Name = $"Student {id}", DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
        Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1), AppUserId = appUserId
    };

    private VlmsDbContext CreateContext(ICurrentUserContext user) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, user);

    private async Task<CertificateFileContent?> OpenAsync(int certificateId, ICurrentUserContext user)
    {
        using var db = CreateContext(user);
        return await new CertificateAccessService(db, _blobs, user).OpenAsync(certificateId);
    }

    [Theory]
    [InlineData(OwnStudentUser, Role.Student)]
    [InlineData(LinkedParentUser, Role.Parent)]
    [InlineData(5, Role.Teacher)]
    [InlineData(6, Role.Admin)]
    public async Task OpenAsync_AllowedCaller_ReturnsThePdf(int userId, Role role)
    {
        var file = await OpenAsync(1, new FakeCurrentUserContext(userId, role));

        Assert.NotNull(file);
        Assert.Equal(Pdf, file.Content);
        Assert.Equal("certificate-1.pdf", file.FileName);
    }

    [Theory]
    [InlineData(UnlinkedParentUser, Role.Parent)]
    [InlineData(LeftParentUser, Role.Parent)]
    [InlineData(OtherStudentUser, Role.Student)]
    [InlineData(OwnStudentUser, Role.Approver)]
    [InlineData(LinkedParentUser, Role.SafeguardingOfficer)]
    public async Task OpenAsync_DisallowedCaller_ReturnsNull(int userId, Role role)
    {
        Assert.Null(await OpenAsync(1, new FakeCurrentUserContext(userId, role)));
    }

    [Fact]
    public async Task OpenAsync_StudentRoleHeldByAParentAccount_DoesNotLetThemInViaTheStudentPath()
    {
        Assert.Null(await OpenAsync(1, new FakeCurrentUserContext(UnlinkedParentUser, Role.Student)));
    }

    [Fact]
    public async Task OpenAsync_AdminWithoutMfa_ReturnsNull()
    {
        Assert.Null(await OpenAsync(1, new FakeCurrentUserContext(6, hasMfaEnabled: false, Role.Admin)));
    }

    [Fact]
    public async Task OpenAsync_NoResolvedUser_ReturnsNull()
    {
        Assert.Null(await OpenAsync(1, new FakeCurrentUserContext(null, Role.Admin)));
    }

    [Fact]
    public async Task OpenAsync_UnknownId_ReturnsNullForAnyone()
    {
        Assert.Null(await OpenAsync(999, new FakeCurrentUserContext(6, Role.Admin)));
        Assert.Null(await OpenAsync(999, new FakeCurrentUserContext(LinkedParentUser, Role.Parent)));
    }

    [Fact]
    public async Task OpenAsync_BlobMissing_ReturnsNull()
    {
        using (var db = CreateContext(new FakeCurrentUserContext(1, Role.Admin)))
        {
            db.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 3, StudentId = 100, LessonId = 1, CompletedByUserId = 1, CompletedAt = DateTime.UtcNow, IsReversed = false });
            db.Certificates.Add(new Certificate { Id = 3, StudentLessonCompletionId = 3, GeneratedAt = DateTime.UtcNow, BlobKey = "certificates/100/missing.pdf" });
            db.SaveChanges();
        }

        Assert.Null(await OpenAsync(3, new FakeCurrentUserContext(5, Role.Teacher)));
    }

    [Theory]
    [InlineData(OwnStudentUser, Role.Student, false)]
    [InlineData(LinkedParentUser, Role.Parent, false)]
    [InlineData(5, Role.Teacher, true)]
    [InlineData(6, Role.Admin, true)]
    public async Task OpenAsync_ReversedCompletionsCertificate_IsHiddenFromStudentAndParentOnly(int userId, Role role, bool expected)
    {
        Assert.Equal(expected, await OpenAsync(2, new FakeCurrentUserContext(userId, role)) is not null);
    }
}
