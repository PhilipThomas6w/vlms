using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Retention;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="LeavingService"/> (STATE.md "Manual 'leaving' flag for students and
/// teachers, feeding the retention report"): the Admin-only action that sets
/// <see cref="Student.Status"/>/<see cref="Student.LeftAt"/> and
/// <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/> — no self-service path for anyone to
/// mark themselves inactive/left, and no cascading effect on anything else (promotions, badges,
/// certificates, existing <see cref="DbsCheck"/> rows).
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="DbsCheckServiceTests"/>.
/// </summary>
public sealed class LeavingServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public LeavingServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-leaving-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public LeavingService Service => new(Context, CurrentUser, scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());

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

    private static void SeedReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });

        context.Students.Add(new Student
        {
            Id = 100,
            Name = "Active Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2023, 1, 1)
        });
        context.Students.Add(new Student
        {
            Id = 101,
            Name = "Already Graduated Student",
            DateOfBirth = new DateOnly(2010, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Graduated,
            EnrolmentDate = new DateOnly(2019, 1, 1)
        });

        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.AppUsers.Add(new AppUser { Id = 20, DisplayName = "Approver One", Email = "approver1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 20, Role = Role.Approver });

        context.SaveChanges();
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_ByAdmin_SetsStatusAndCapturesLeftAtAutomatically()
    {
        using var admin = CreateContext(1, Role.Admin);
        var before = DateTime.UtcNow;

        var student = await admin.Service.MarkStudentInactiveAsync(100, CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.Equal(StudentStatus.Inactive, student.Status);
        Assert.NotNull(student.LeftAt);
        Assert.InRange(student.LeftAt!.Value, before, after);

        using var verify = CreateContext(1, Role.Admin);
        var persisted = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Inactive, persisted.Status);
        Assert.Equal(student.LeftAt, persisted.LeftAt);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task MarkStudentInactiveAsync_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(2, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Service.MarkStudentInactiveAsync(100, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillActive = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Active, stillActive.Status);
        Assert.Null(stillActive.LeftAt);
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_AlreadyGraduatedStudent_Throws_AndChangesNothing()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStudentInactiveAsync(101, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillGraduated = await verify.Context.Students.SingleAsync(s => s.Id == 101);
        Assert.Equal(StudentStatus.Graduated, stillGraduated.Status);
        Assert.Null(stillGraduated.LeftAt);
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_AlreadyInactiveStudent_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkStudentInactiveAsync(100, CancellationToken.None);
        }

        using var secondAttempt = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondAttempt.Service.MarkStudentInactiveAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task MarkStudentInactiveAsync_UnknownStudent_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkStudentInactiveAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task MarkTeacherLeftAsync_ByAdmin_SetsHasLeftAndCapturesLeftAtAutomatically()
    {
        using var admin = CreateContext(1, Role.Admin);
        var before = DateTime.UtcNow;

        var teacher = await admin.Service.MarkTeacherLeftAsync(10, CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.True(teacher.HasLeft);
        Assert.NotNull(teacher.LeftAt);
        Assert.InRange(teacher.LeftAt!.Value, before, after);

        using var verify = CreateContext(1, Role.Admin);
        var persisted = await verify.Context.AppUsers.SingleAsync(u => u.Id == 10);
        Assert.True(persisted.HasLeft);
        Assert.Equal(teacher.LeftAt, persisted.LeftAt);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task MarkTeacherLeftAsync_ByNonAdminRole_Throws_AndChangesNothing(Role role)
    {
        using var caller = CreateContext(2, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => caller.Service.MarkTeacherLeftAsync(10, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillThere = await verify.Context.AppUsers.SingleAsync(u => u.Id == 10);
        Assert.False(stillThere.HasLeft);
        Assert.Null(stillThere.LeftAt);
    }

    [Fact]
    public async Task MarkTeacherLeftAsync_NonTeacherAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        // AppUser 20 holds Approver, not Teacher.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkTeacherLeftAsync(20, CancellationToken.None));
    }

    [Fact]
    public async Task MarkTeacherLeftAsync_UnknownAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => admin.Service.MarkTeacherLeftAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task MarkTeacherLeftAsync_AlreadyLeft_Throws()
    {
        using (var admin = CreateContext(1, Role.Admin))
        {
            await admin.Service.MarkTeacherLeftAsync(10, CancellationToken.None);
        }

        using var secondAttempt = CreateContext(1, Role.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondAttempt.Service.MarkTeacherLeftAsync(10, CancellationToken.None));
    }
}
