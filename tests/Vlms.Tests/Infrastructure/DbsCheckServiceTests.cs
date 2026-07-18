using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies DBS-check management (STATE.md, functional.md FR-002, data-design.md
/// <see cref="DbsCheck"/>, adr/0004-sensitive-data-access-control.md): only
/// Admin/SafeguardingOfficer may record or update a <see cref="DbsCheck"/> — Teacher and Approver
/// must be denied entirely, matching FR-002's own wording.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="ConsentRecordServiceTests"/>.
/// </summary>
public sealed class DbsCheckServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public DbsCheckServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-dbscheck-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public DbsCheckService Service => new(Context, CurrentUser);

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
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "safeguarding-1", DisplayName = "Safeguarding One", Email = "sg@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        context.SaveChanges();
    }

    [Fact]
    public async Task RecordAsync_ByAdmin_CreatesDbsCheck()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var check = await sut.RecordAsync(
            teacherUserId: 10,
            checkDate: new DateOnly(2025, 1, 1),
            expiryDate: new DateOnly(2028, 1, 1),
            certificateNumber: "CERT-001",
            status: DbsCheckStatus.Clear,
            CancellationToken.None);

        Assert.Equal(10, check.TeacherUserId);
        Assert.Equal(DbsCheckStatus.Clear, check.Status);

        using var verify = CreateContext(1, Role.Admin);
        Assert.Single(verify.Context.DbsChecks.Where(d => d.TeacherUserId == 10));
    }

    [Fact]
    public async Task RecordAsync_BySafeguardingOfficer_Succeeds()
    {
        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        var sut = sg.Service;

        var check = await sut.RecordAsync(
            10, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-002", DbsCheckStatus.Pending, CancellationToken.None);

        Assert.Equal(DbsCheckStatus.Pending, check.Status);
    }

    [Fact]
    public async Task RecordAsync_ByTeacher_Throws_AndCreatesNothing()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RecordAsync(
            10, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-003", DbsCheckStatus.Clear, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.DbsChecks.ToList());
    }

    [Fact]
    public async Task RecordAsync_ByApprover_Throws_AndCreatesNothing()
    {
        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RecordAsync(
            10, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-004", DbsCheckStatus.Clear, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.DbsChecks.ToList());
    }

    [Fact]
    public async Task RecordAsync_NonTeacherAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        // AppUser 1 (Admin) does not hold the Teacher role.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RecordAsync(
            1, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-005", DbsCheckStatus.Clear, CancellationToken.None));
    }

    [Fact]
    public async Task RecordAsync_UnknownAppUser_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RecordAsync(
            9999, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-006", DbsCheckStatus.Clear, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatusAsync_ByAdmin_UpdatesStatus()
    {
        int checkId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            var check = await admin.Service.RecordAsync(
                10, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-007", DbsCheckStatus.Pending, CancellationToken.None);
            checkId = check.Id;
        }

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        var updated = await sg.Service.UpdateStatusAsync(checkId, DbsCheckStatus.Clear, CancellationToken.None);

        Assert.Equal(DbsCheckStatus.Clear, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_ByTeacher_Throws()
    {
        int checkId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            var check = await admin.Service.RecordAsync(
                10, new DateOnly(2025, 1, 1), new DateOnly(2028, 1, 1), "CERT-008", DbsCheckStatus.Pending, CancellationToken.None);
            checkId = check.Id;
        }

        using var teacher = CreateContext(10, Role.Teacher);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => teacher.Service.UpdateStatusAsync(checkId, DbsCheckStatus.Clear, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillPending = await verify.Context.DbsChecks.SingleAsync(d => d.Id == checkId);
        Assert.Equal(DbsCheckStatus.Pending, stillPending.Status);
    }
}
