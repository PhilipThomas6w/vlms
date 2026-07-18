using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Progress;
using Vlms.Infrastructure.Registration;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies student registration/enrolment (docs/design/data-design.md Student/StudentRankProgress,
/// STATE.md): registering a student creates the Student row, opens their first
/// StudentRankProgress row at the starting rank (smallest Rank.Order), and creates a
/// StudentGuardianLink via the existing GuardianLinkService (both entry points) — Admin/Teacher
/// only, never Parent self-service, matching GuardianLinkServiceTests' pattern.
///
/// Also proves the property that actually matters for
/// <see cref="Vlms.Infrastructure.Progress.PromotionService"/>'s documented precondition: the
/// StudentRankProgress row opened here satisfies its "open row for the current rank" lookup
/// exactly, so a freshly registered student can be driven through
/// <see cref="PromotionService.CheckAndPromoteAsync"/> without hitting the
/// "no open StudentRankProgress row" InvalidOperationException.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="GuardianLinkServiceTests"/>.
/// </summary>
public sealed class StudentRegistrationServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public StudentRegistrationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-studentreg-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public StudentRegistrationService Service => new(Context, CurrentUser, new GuardianLinkService(Context, CurrentUser));

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
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" }); // lowest Order — the starting rank
        context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Explorer" });
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 99, EntraObjectId = "parent-1", DisplayName = "Parent One", Email = "parent1@example.com" });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "jane@example.com", IsPrimary = true });
        context.SaveChanges();
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_ByAdmin_CreatesStudentProgressAndLink()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var (student, progress, guardian, link) = await sut.RegisterStudentWithNewGuardianAsync(
            "Alex Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
            "New Parent", "new@example.com", guardianIsPrimary: true, CancellationToken.None);

        Assert.Equal("Alex Student", student.Name);
        Assert.Equal(1, student.CurrentRankId); // starting rank = smallest Rank.Order
        Assert.Equal(StudentStatus.Active, student.Status);
        Assert.Equal(1, progress.RankId);
        Assert.Null(progress.CompletedAt);
        Assert.Equal("New Parent", guardian.Name);
        Assert.Equal(student.Id, link.StudentId);
        Assert.Equal(guardian.Id, link.ParentGuardianId);

        using var verify = CreateContext(1, Role.Admin);
        var savedStudent = await verify.Context.Students.SingleAsync(s => s.Id == student.Id);
        Assert.Equal(1, savedStudent.CurrentRankId);

        var savedProgress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == student.Id);
        Assert.Equal(1, savedProgress.RankId);
        Assert.Null(savedProgress.CompletedAt);

        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == student.Id));
        Assert.Single(verify.Context.ParentGuardians.Where(g => g.Name == "New Parent"));
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_ByTeacher_Succeeds()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        var (student, _, _, link) = await sut.RegisterStudentWithNewGuardianAsync(
            "Sam Student", new DateOnly(2013, 1, 1), new DateOnly(2024, 2, 1), assignedTeacherUserId: 10,
            "Another Parent", "another@example.com", guardianIsPrimary: false, CancellationToken.None);

        Assert.Equal(10, link.CreatedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.Students.Where(s => s.Id == student.Id));
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_ByParent_Throws_AndCreatesNoRows()
    {
        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RegisterStudentWithNewGuardianAsync(
                "Sneaky Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "Sneaky Parent", "sneaky@example.com", guardianIsPrimary: false, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.Students.Where(s => s.Name == "Sneaky Student"));
        Assert.Empty(verify.Context.StudentRankProgresses);
        Assert.Empty(verify.Context.ParentGuardians.Where(g => g.Name == "Sneaky Parent"));
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_ByApprover_Throws()
    {
        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RegisterStudentWithNewGuardianAsync(
                "Nope Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "Nope Parent", "nope@example.com", guardianIsPrimary: false, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.Students.Where(s => s.Name == "Nope Student"));
        Assert.Empty(verify.Context.StudentRankProgresses);
        Assert.Empty(verify.Context.ParentGuardians.Where(g => g.Name == "Nope Parent"));
    }

    [Fact]
    public async Task RegisterStudentWithExistingGuardianAsync_ByAdmin_CreatesStudentProgressAndLink()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var (student, progress, link) = await sut.RegisterStudentWithExistingGuardianAsync(
            "Jamie Student", new DateOnly(2011, 1, 1), new DateOnly(2024, 3, 1), assignedTeacherUserId: null,
            parentGuardianId: 1, CancellationToken.None);

        Assert.Equal(1, progress.RankId);
        Assert.Null(progress.CompletedAt);
        Assert.Equal(1, link.ParentGuardianId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.StudentGuardianLinks.Where(l => l.StudentId == student.Id && l.ParentGuardianId == 1));
        // No duplicate ParentGuardian created — the existing one (Id 1) was reused.
        Assert.Single(verify.Context.ParentGuardians.Where(g => g.Id == 1));
    }

    [Fact]
    public async Task RegisterStudentWithExistingGuardianAsync_ByParent_Throws_AndCreatesNoStudent()
    {
        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RegisterStudentWithExistingGuardianAsync(
                "Sneaky Student 2", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                parentGuardianId: 1, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.Students.Where(s => s.Name == "Sneaky Student 2"));
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_BlankName_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RegisterStudentWithNewGuardianAsync(
                "   ", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "New Parent", "new@example.com", guardianIsPrimary: false, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_BlankGuardianName_ThrowsAndCreatesNoRows()
    {
        // The atomicity property that matters here: CreateStudentAndOpenRankProgressAsync's
        // SaveChangesAsync already ran (Student/StudentRankProgress pending) by the time
        // GuardianLinkService.RegisterGuardianAndLinkAsync's ArgumentException.ThrowIfNullOrWhiteSpace
        // throws on the blank guardian name. Both entry points wrap the whole operation in one
        // Database.BeginTransactionAsync/CommitAsync, so the un-committed transaction must roll back
        // and leave zero rows of any kind — not an orphaned Student with no guardian link.
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.RegisterStudentWithNewGuardianAsync(
                "Orphan Risk Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "   ", "new@example.com", guardianIsPrimary: false, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.Students.Where(s => s.Name == "Orphan Risk Student"));
        Assert.Empty(verify.Context.StudentRankProgresses);
        Assert.Empty(verify.Context.StudentGuardianLinks);
        Assert.Empty(verify.Context.ParentGuardians.Where(g => g.ContactInfo == "new@example.com"));
    }

    [Fact]
    public async Task RegisterStudentWithExistingGuardianAsync_UnknownParentGuardianId_ThrowsAndCreatesNoRows()
    {
        // Same atomicity property via the other entry point: GuardianLinkService.CreateLinkAsync's
        // SingleAsync lookup throws on an unknown parentGuardianId, after the Student/
        // StudentRankProgress rows already exist (uncommitted) in the same shared transaction.
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RegisterStudentWithExistingGuardianAsync(
                "Orphan Risk Student 2", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                parentGuardianId: 999, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.Students.Where(s => s.Name == "Orphan Risk Student 2"));
        Assert.Empty(verify.Context.StudentRankProgresses);
        Assert.Empty(verify.Context.StudentGuardianLinks);
    }

    [Fact]
    public async Task RegisterStudentWithNewGuardianAsync_UnknownGuardianScenario_NoRanksConfigured_Throws()
    {
        // A separate empty database with no Rank reference data at all — proves the starting-rank
        // lookup fails clearly rather than fabricating a Rank.
        var connectionString = $"Data Source=file:vlms-studentreg-norank-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var anchor = new SqliteConnection(connectionString);
        anchor.Open();

        var services = new ServiceCollection();
        var currentUser = new FakeCurrentUserContext(1, Role.Admin);
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(connectionString));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        context.Database.EnsureCreated();

        var sut = new StudentRegistrationService(context, currentUser, new GuardianLinkService(context, currentUser));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RegisterStudentWithNewGuardianAsync(
                "No Rank Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "New Parent", "new@example.com", guardianIsPrimary: false, CancellationToken.None));

        Assert.Empty(context.Students);
    }

    [Fact]
    public async Task RegisterThenComplete_DrivesThroughPromotionService_WithoutThrowing()
    {
        // The property that actually matters: the StudentRankProgress row opened at registration
        // must satisfy PromotionService.CheckAndPromoteAsync's "open row for the current rank"
        // precondition, or that call throws InvalidOperationException (see PromotionService's doc
        // comment). Registers a student, gives them the one active Lesson at their starting rank,
        // marks it complete, then drives them through PromotionService successfully.
        int studentId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            admin.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            admin.Context.SaveChanges();

            var (registeredStudent, _, _, _) = await admin.Service.RegisterStudentWithNewGuardianAsync(
                "Promotable Student", new DateOnly(2012, 1, 1), new DateOnly(2024, 1, 1), assignedTeacherUserId: null,
                "New Parent", "new@example.com", guardianIsPrimary: true, CancellationToken.None);
            studentId = registeredStudent.Id;

            admin.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = 1,
                StudentId = studentId,
                LessonId = 1,
                CompletedByUserId = 1,
                CompletedAt = DateTime.UtcNow,
                IsReversed = false,
                ReversedAt = null
            });
            admin.Context.SaveChanges();
        }

        using var caller = CreateContext(1, Role.Admin);
        var promotionService = new PromotionService(caller.Context);

        // The exception under test would be InvalidOperationException("... has no open
        // StudentRankProgress row ..."), thrown if registration hadn't opened one correctly.
        var promoted = await promotionService.CheckAndPromoteAsync(studentId, CancellationToken.None);

        Assert.True(promoted);

        using var verify = CreateContext(1, Role.Admin);
        var student = await verify.Context.Students.SingleAsync(s => s.Id == studentId);
        Assert.Equal(2, student.CurrentRankId); // promoted to the next rank

        var closedProgress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == studentId && p.RankId == 1);
        Assert.NotNull(closedProgress.CompletedAt);

        var openProgress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == studentId && p.RankId == 2);
        Assert.Null(openProgress.CompletedAt);
    }
}
