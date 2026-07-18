using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies consent-record management (STATE.md, functional.md FR-001, data-design.md
/// ConsentRecord/ConsentSensitiveDetails split, adr/0004-sensitive-data-access-control.md): only
/// Admin/SafeguardingOfficer may record or decide a <see cref="ConsentRecord"/> — the Approver role
/// (curriculum-only) and Teacher must be denied, and the existing whole-entity query filter/
/// read-audit mechanism on <see cref="ConsentSensitiveDetails"/> must keep working unchanged.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="GuardianLinkServiceTests"/>.
/// </summary>
public sealed class ConsentRecordServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ConsentRecordServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-consent-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

        public ConsentRecordService Service => new(Context, CurrentUser);

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
        context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "safeguarding-1", DisplayName = "Safeguarding One", Email = "sg@example.com" });
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
        context.SaveChanges();
    }

    [Fact]
    public async Task RecordAsync_ByAdmin_CreatesConsentRecordAndSensitiveDetails()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        var record = await sut.RecordAsync(
            studentId: 100,
            periodStart: new DateOnly(2026, 1, 1),
            periodEnd: new DateOnly(2026, 12, 31),
            expiryDate: new DateOnly(2026, 12, 31),
            photoMediaConsent: true,
            transportOffsiteConsent: true,
            dataSharingConsent: false,
            submittedByParentId: 1,
            emergencyContact: "Jane Parent, 07000 000000",
            emergencyMedicalInfo: "Asthma",
            dietarySEN: "No nuts",
            CancellationToken.None);

        Assert.Equal(ConsentStatus.Pending, record.Status);
        Assert.Null(record.ApprovedByUserId);

        using var verify = CreateContext(1, Role.Admin);
        var storedRecord = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == record.Id);
        Assert.Equal(100, storedRecord.StudentId);

        var details = await verify.Context.ConsentSensitiveDetails.SingleAsync(d => d.ConsentRecordId == record.Id);
        Assert.Equal("Jane Parent, 07000 000000", details.EmergencyContact);
        Assert.Equal("Asthma", details.EmergencyMedicalInfo);
    }

    [Fact]
    public async Task RecordAsync_BySafeguardingOfficer_Succeeds()
    {
        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        var sut = sg.Service;

        var record = await sut.RecordAsync(
            100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "Emergency contact", null, null, CancellationToken.None);

        Assert.Equal(ConsentStatus.Pending, record.Status);
    }

    [Fact]
    public async Task RecordAsync_ByTeacher_Throws_AndCreatesNothing()
    {
        using var teacher = CreateContext(10, Role.Teacher);
        var sut = teacher.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RecordAsync(
            100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "Emergency contact", null, null, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.ConsentRecords.Where(c => c.StudentId == 100));
        Assert.Empty(verify.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public async Task RecordAsync_ByApprover_Throws_AndCreatesNothing()
    {
        // The Approver role is curriculum-only, never safeguarding/consent sign-off — hard
        // constraint (CLAUDE.md Project Law, functional.md, VISION.md).
        using var approver = CreateContext(20, Role.Approver);
        var sut = approver.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RecordAsync(
            100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "Emergency contact", null, null, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        Assert.Empty(verify.Context.ConsentRecords.Where(c => c.StudentId == 100));
        Assert.Empty(verify.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public async Task RecordAsync_ByParent_Throws()
    {
        using var parent = CreateContext(99, Role.Parent);
        var sut = parent.Service;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.RecordAsync(
            100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "Emergency contact", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RecordAsync_BlankEmergencyContact_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RecordAsync(
            100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "   ", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RecordAsync_UnknownStudent_Throws()
    {
        using var admin = CreateContext(1, Role.Admin);
        var sut = admin.Service;

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RecordAsync(
            9999, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
            true, true, true, 1, "Emergency contact", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task DecideAsync_Approve_SetsStatusAndApprover()
    {
        int recordId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            var record = await admin.Service.RecordAsync(
                100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
                true, true, true, 1, "Emergency contact", null, null, CancellationToken.None);
            recordId = record.Id;
        }

        using var sg = CreateContext(2, Role.SafeguardingOfficer);
        var decided = await sg.Service.DecideAsync(recordId, approve: true, CancellationToken.None);

        Assert.Equal(ConsentStatus.Approved, decided.Status);
        Assert.Equal(2, decided.ApprovedByUserId);
    }

    [Fact]
    public async Task DecideAsync_Reject_SetsStatusRejected()
    {
        int recordId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            var record = await admin.Service.RecordAsync(
                100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
                true, true, true, 1, "Emergency contact", null, null, CancellationToken.None);
            recordId = record.Id;
        }

        using var admin2 = CreateContext(1, Role.Admin);
        var decided = await admin2.Service.DecideAsync(recordId, approve: false, CancellationToken.None);

        Assert.Equal(ConsentStatus.Rejected, decided.Status);
    }

    [Fact]
    public async Task DecideAsync_ByApprover_Throws()
    {
        int recordId;
        using (var admin = CreateContext(1, Role.Admin))
        {
            var record = await admin.Service.RecordAsync(
                100, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31),
                true, true, true, 1, "Emergency contact", null, null, CancellationToken.None);
            recordId = record.Id;
        }

        using var approver = CreateContext(20, Role.Approver);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => approver.Service.DecideAsync(recordId, approve: true, CancellationToken.None));

        using var verify = CreateContext(1, Role.Admin);
        var stillPending = await verify.Context.ConsentRecords.SingleAsync(c => c.Id == recordId);
        Assert.Equal(ConsentStatus.Pending, stillPending.Status);
    }
}
