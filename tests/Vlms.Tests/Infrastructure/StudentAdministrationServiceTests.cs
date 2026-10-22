using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Administration;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="StudentAdministrationService"/> (functional.md FR-013): only an Admin assigns
/// a student's teacher or changes a student's status, and status changes stay consistent with
/// <see cref="LeavingService"/> and <see cref="RetentionFlagging"/>.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LeavingServiceTests"/>.
/// </summary>
public sealed class StudentAdministrationServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public StudentAdministrationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-studentadmin-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public StudentAdministrationService Service => new(Context, currentUser, new LeavingService(Context, currentUser, scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>()));

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
        services.AddLogging();
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context, currentUser);
    }

    private static Student NewStudent(int id, string name, StudentStatus status) => new()
    {
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2012, 1, 1),
        CurrentRankId = 1,
        Status = status,
        EnrolmentDate = new DateOnly(2023, 1, 1)
    };

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });

        context.Students.Add(NewStudent(100, "Active Student", StudentStatus.Active));
        context.Students.Add(NewStudent(101, "Graduated Student", StudentStatus.Graduated));
        context.Students.Add(NewStudent(102, "Inactive Student", StudentStatus.Inactive));

        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.AppUsers.Add(new AppUser { Id = 11, DisplayName = "Teacher Left", Email = "teacher2@example.com", HasLeft = true, LeftAt = DateTime.UtcNow });
        context.UserRoles.Add(new UserRole { UserId = 11, Role = Role.Teacher });
        context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Approver One", Email = "approver1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 20, Role = Role.Approver });

        context.SaveChanges();
    }

    [Fact]
    public async Task AssignTeacherAsync_ByAdmin_AssignsThenClears()
    {
        using var admin = CreateContext(1, Role.Admin);

        var assigned = await admin.Service.AssignTeacherAsync(100, 10);
        Assert.Equal(10, assigned.AssignedTeacherUserId);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(10, (await verify.Context.Students.SingleAsync(s => s.Id == 100)).AssignedTeacherUserId);

        var cleared = await admin.Service.AssignTeacherAsync(100, null);
        Assert.Null(cleared.AssignedTeacherUserId);

        using var verifyAgain = CreateContext(1, Role.Admin);
        Assert.Null((await verifyAgain.Context.Students.SingleAsync(s => s.Id == 100)).AssignedTeacherUserId);
    }

    [Fact]
    public async Task AssignTeacherAsync_UserWithoutTeacherRole_Throws_AndChangesNothing()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.AssignTeacherAsync(100, 20));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Null((await verify.Context.Students.SingleAsync(s => s.Id == 100)).AssignedTeacherUserId);
    }

    [Fact]
    public async Task AssignTeacherAsync_TeacherWhoHasLeft_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.AssignTeacherAsync(100, 11));
    }

    [Fact]
    public async Task AssignTeacherAsync_UnknownUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.AssignTeacherAsync(100, 999));
    }

    [Fact]
    public async Task ListAssignableTeachersAsync_ExcludesLeftTeachersAndNonTeachers()
    {
        using var admin = CreateContext(1, Role.Admin);

        var teachers = await admin.Service.ListAssignableTeachersAsync();

        Assert.Equal([10], teachers.Select(t => t.UserId));
    }

    [Fact]
    public async Task ListStudentsAsync_ShowsRankStatusAndAssignedTeacher()
    {
        using var admin = CreateContext(1, Role.Admin);
        await admin.Service.AssignTeacherAsync(100, 10);

        var students = await admin.Service.ListStudentsAsync();

        var row = Assert.Single(students, s => s.StudentId == 100);
        Assert.Equal("Recruit", row.RankName);
        Assert.Equal(StudentStatus.Active, row.Status);
        Assert.Equal("Teacher One", row.AssignedTeacherName);
        Assert.Equal(3, students.Count);
    }

    [Fact]
    public async Task ChangeStatusAsync_ActiveToInactive_SetsLeftAt_AndRetentionReportPicksItUp()
    {
        using var admin = CreateContext(1, Role.Admin);

        var student = await admin.Service.ChangeStatusAsync(100, StudentStatus.Inactive);

        Assert.Equal(StudentStatus.Inactive, student.Status);
        Assert.NotNull(student.LeftAt);

        using var verify = CreateContext(1, Role.Admin);
        var flags = await RetentionFlagging.GetStudentsDueForDeletionAsync(
            verify.Context, DateTime.UtcNow.AddYears(4), CancellationToken.None);
        var flag = Assert.Single(flags, f => f.StudentId == 100);
        Assert.Equal(StudentDeletionReason.Left, flag.Reason);
        Assert.Equal(student.LeftAt, flag.AnchorDate);
    }

    [Fact]
    public async Task ChangeStatusAsync_ActiveToInactive_KeepsAssignedTeacher()
    {
        using var admin = CreateContext(1, Role.Admin);
        await admin.Service.AssignTeacherAsync(100, 10);

        var student = await admin.Service.ChangeStatusAsync(100, StudentStatus.Inactive);

        Assert.Equal(10, student.AssignedTeacherUserId);
    }

    [Fact]
    public async Task ChangeStatusAsync_InactiveToActive_ClearsLeftAt_AndStopsRetentionFlagging()
    {
        using var admin = CreateContext(1, Role.Admin);
        await admin.Service.ChangeStatusAsync(100, StudentStatus.Inactive);

        var student = await admin.Service.ChangeStatusAsync(100, StudentStatus.Active);

        Assert.Equal(StudentStatus.Active, student.Status);
        Assert.Null(student.LeftAt);

        using var verify = CreateContext(1, Role.Admin);
        var persisted = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Active, persisted.Status);
        Assert.Null(persisted.LeftAt);
        var flags = await RetentionFlagging.GetStudentsDueForDeletionAsync(
            verify.Context, DateTime.UtcNow.AddYears(4), CancellationToken.None);
        Assert.DoesNotContain(flags, f => f.StudentId == 100);

        // Leaving again records a fresh date through LeavingService.
        var left = await admin.Service.ChangeStatusAsync(100, StudentStatus.Inactive);
        Assert.NotNull(left.LeftAt);
    }

    [Theory]
    [InlineData(100)] // Active
    [InlineData(102)] // Inactive
    public async Task ChangeStatusAsync_ToGraduated_IsRefused(int studentId)
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.ChangeStatusAsync(studentId, StudentStatus.Graduated));
    }

    [Theory]
    [InlineData(StudentStatus.Active)]
    [InlineData(StudentStatus.Inactive)]
    public async Task ChangeStatusAsync_FromGraduated_IsRefused(StudentStatus target)
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ChangeStatusAsync(101, target));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Equal(StudentStatus.Graduated, (await verify.Context.Students.SingleAsync(s => s.Id == 101)).Status);
    }

    [Fact]
    public async Task ChangeStatusAsync_SameStatus_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.ChangeStatusAsync(100, StudentStatus.Active));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Parent)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Approver)]
    public async Task EveryMethod_ForNonAdmin_IsRefused_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(10, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListStudentsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListAssignableTeachersAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.AssignTeacherAsync(100, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.AssignTeacherAsync(100, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ChangeStatusAsync(100, StudentStatus.Inactive));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ChangeStatusAsync(102, StudentStatus.Active));

        using var verify = CreateContext(1, Role.Admin);
        var active = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Active, active.Status);
        Assert.Null(active.AssignedTeacherUserId);
        Assert.Equal(StudentStatus.Inactive, (await verify.Context.Students.SingleAsync(s => s.Id == 102)).Status);
    }
}
