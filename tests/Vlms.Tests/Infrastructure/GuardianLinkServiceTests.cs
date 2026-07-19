using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Guardianship;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies guardian-link creation (docs/design/low-level-design.md "Authorization model",
/// docs/design/data-design.md "Guardian link verification", docs/requirements/functional.md
/// FR-004, STATE.md): only Admin/Teacher may create a <see cref="StudentGuardianLink"/> — a Parent
/// must never be able to self-declare a relationship to a child — and both entry points
/// (linking an existing guardian, and registering a new one in the same call) behave correctly.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LessonProposalServiceTests"/>.
/// </summary>
public sealed class GuardianLinkServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public GuardianLinkServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-guardianlink-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public GuardianLinkService Service => new(Context, CurrentUser);

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
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 99, EntraObjectId = "parent-1", DisplayName = "Parent One", Email = "parent1@example.com" });
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
        context.Students.Add(new Student
        {
            Id = 101,
            Name = "Sam Student",
            DateOfBirth = new DateOnly(2013, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task CreateLinkAsync_ByAdmin_LinksExistingGuardian()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var link = await sut.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None);

        Assert.Equal(100, link.StudentId);
        Assert.Equal(1, link.ParentGuardianId);
        Assert.Equal(1, link.CreatedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100 && l.ParentGuardianId == 1));
    }

    [Fact]
    public async Task CreateLinkAsync_ByTeacher_LinksExistingGuardian()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        var link = await sut.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None);

        Assert.Equal(10, link.CreatedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100 && l.ParentGuardianId == 1));
    }

    [Fact]
    public async Task CreateLinkAsync_ByParent_Throws_AndCreatesNoLink()
    {
        // The hard constraint under test: a parent must never be able to self-declare a
        // relationship to a child (CLAUDE.md Project Law; functional.md FR-004).
        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100));
    }

    [Fact]
    public async Task CreateLinkAsync_ByApprover_Throws()
    {
        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None));
    }

    [Fact]
    public async Task CreateLinkAsync_DuplicateLink_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None);
        }

        using var second = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.Service.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100 && l.ParentGuardianId == 1));
    }

    [Fact]
    public async Task CreateLinkAsync_SameGuardian_DifferentStudent_Succeeds()
    {
        // Not a duplicate: the composite key is (StudentId, ParentGuardianId) — the same guardian
        // legitimately links to more than one child (data-design.md).
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await sut.CreateLinkAsync(studentId: 100, parentGuardianId: 1, CancellationToken.None);
        await sut.CreateLinkAsync(studentId: 101, parentGuardianId: 1, CancellationToken.None);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(2, verify.Context.StudentGuardianLinks.Count(l => l.ParentGuardianId == 1));
    }

    [Fact]
    public async Task CreateLinkAsync_UnknownStudent_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateLinkAsync(studentId: 9999, parentGuardianId: 1, CancellationToken.None));
    }

    [Fact]
    public async Task CreateLinkAsync_UnknownGuardian_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateLinkAsync(studentId: 100, parentGuardianId: 9999, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterGuardianAndLinkAsync_ByAdmin_CreatesGuardianAndLink()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var (guardian, link) = await sut.RegisterGuardianAndLinkAsync(
            studentId: 100, guardianName: "New Parent", contactInfo: "new@example.com", isPrimary: true, CancellationToken.None);

        Assert.Equal("New Parent", guardian.Name);
        Assert.True(guardian.IsPrimary);
        Assert.Equal(100, link.StudentId);
        Assert.Equal(guardian.Id, link.ParentGuardianId);
        Assert.Equal(1, link.CreatedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.ParentGuardians.Where(g => g.Name == "New Parent"));
        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100 && l.ParentGuardianId == guardian.Id));
    }

    [Fact]
    public async Task RegisterGuardianAndLinkAsync_ByTeacher_CreatesGuardianAndLink()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        var (guardian, link) = await sut.RegisterGuardianAndLinkAsync(
            studentId: 100, guardianName: "Another Parent", contactInfo: "another@example.com", isPrimary: false, CancellationToken.None);

        Assert.Equal(10, link.CreatedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.ParentGuardians.Where(g => g.Id == guardian.Id));
    }

    [Fact]
    public async Task RegisterGuardianAndLinkAsync_ByParent_Throws_AndCreatesNoGuardianOrLink()
    {
        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RegisterGuardianAndLinkAsync(
                studentId: 100, guardianName: "Sneaky Parent", contactInfo: "sneaky@example.com", isPrimary: false, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.ParentGuardians.Where(g => g.Name == "Sneaky Parent"));
        Assert.Empty(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == 100));
    }

    [Fact]
    public async Task RegisterGuardianAndLinkAsync_BlankName_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RegisterGuardianAndLinkAsync(
                studentId: 100, guardianName: "  ", contactInfo: "contact@example.com", isPrimary: false, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterGuardianAndLinkAsync_UnknownStudent_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RegisterGuardianAndLinkAsync(
                studentId: 9999, guardianName: "New Parent", contactInfo: "new@example.com", isPrimary: false, CancellationToken.None));
    }
}
