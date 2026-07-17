using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the mechanism from adr/0004-sensitive-data-access-control.md: the whole-entity
/// query filters on <see cref="DbsCheck"/>/<see cref="ConsentSensitiveDetails"/>, the
/// <see cref="ConsentRecord"/>/<see cref="ConsentSensitiveDetails"/> split (Teacher keeps
/// Status/ExpiryDate access), and the per-row <see cref="SensitiveDataAccessLog"/> write via
/// the materialization interceptor.
///
/// Uses a SQLite in-memory database rather than the EF Core InMemory provider, because
/// query-filter and relational behaviour needs to be exercised faithfully, not approximated.
///
/// Each simulated "caller" is a real `VlmsDbContext` resolved from its own small DI container
/// (via `AddDbContext`), not a bare `new VlmsDbContext(...)` — this is what makes
/// `materializationData.Context.GetService&lt;ICurrentUserContext&gt;()` inside the audit
/// interceptor resolvable at all (EF Core only wires a context's internal service provider back
/// to an application `IServiceProvider` when the context is constructed through DI), so it
/// exercises the same resolution path production will use once `Vlms.Web` registers the context.
///
/// Every context (including the one the audit interceptor opens internally to persist a log row
/// while the read is still mid-enumeration) is given the *connection string*, not a shared
/// `SqliteConnection` instance — EF's Sqlite provider re-registers custom SQL functions on
/// whichever physical connection it wraps, which SQLite refuses while that connection has an
/// active statement (the open reader from the read being audited). Using a named, shared-cache
/// in-memory database (kept alive by one long-lived anchor connection for the test's duration)
/// lets each context open its own physical connection while still seeing the same data — exactly
/// how separate connections/contexts behave against the real Azure SQL target in production.
/// </summary>
public sealed class SensitiveDataAccessControlTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public SensitiveDataAccessControlTests()
    {
        _connectionString = $"Data Source=file:vlms-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schemaContext = CreateContext(Role.Admin);
        schemaContext.Context.Database.EnsureCreated();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(params Role[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(userId: 1, roles));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context);
    }

    private static ConsentRecord NewConsentRecord(int id, int studentId) => new()
    {
        Id = id,
        StudentId = studentId,
        PeriodStart = new DateOnly(2026, 1, 1),
        PeriodEnd = new DateOnly(2026, 12, 31),
        PhotoMediaConsent = true,
        TransportOffsiteConsent = true,
        DataSharingConsent = true,
        Status = ConsentStatus.Approved,
        SubmittedByParentId = 1,
        ApprovedByUserId = 2,
        ExpiryDate = new DateOnly(2026, 12, 31)
    };

    private static DbsCheck NewDbsCheck(int id, int teacherUserId) => new()
    {
        Id = id,
        TeacherUserId = teacherUserId,
        CheckDate = new DateOnly(2025, 1, 1),
        ExpiryDate = new DateOnly(2028, 1, 1),
        CertificateNumber = $"CERT-{id}",
        Status = DbsCheckStatus.Clear
    };

    private static void SeedCoreReferenceData(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "parent-1", DisplayName = "Parent One", Email = "parent1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "admin-1", DisplayName = "Admin One", Email = "admin1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Parent One", ContactInfo = "parent1@example.com", IsPrimary = true });
        context.Students.Add(new Student
        {
            Id = 1,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2015, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.SaveChanges();
    }

    [Fact]
    public void Teacher_CannotRead_DbsCheck_Or_ConsentSensitiveDetails()
    {
        using (var seed = CreateContext(Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));

            var consent = NewConsentRecord(id: 1, studentId: 1);
            seed.Context.ConsentRecords.Add(consent);
            seed.Context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
            {
                Id = 1,
                ConsentRecordId = consent.Id,
                EmergencyContact = "Someone, 07000 000000"
            });
            seed.Context.SaveChanges();
        }

        using var teacher = CreateContext(Role.Teacher);

        Assert.Empty(teacher.Context.DbsChecks.ToList());
        Assert.Empty(teacher.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void Admin_CanRead_DbsCheck_And_ConsentSensitiveDetails()
    {
        using (var seed = CreateContext(Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));

            var consent = NewConsentRecord(id: 1, studentId: 1);
            seed.Context.ConsentRecords.Add(consent);
            seed.Context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
            {
                Id = 1,
                ConsentRecordId = consent.Id,
                EmergencyContact = "Someone, 07000 000000"
            });
            seed.Context.SaveChanges();
        }

        using var admin = CreateContext(Role.Admin);

        Assert.Single(admin.Context.DbsChecks.ToList());
        Assert.Single(admin.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void Teacher_CanStillRead_ConsentRecord_StatusAndExpiry_ForSameStudent()
    {
        var expiry = new DateOnly(2026, 12, 31);

        using (var seed = CreateContext(Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            var consent = NewConsentRecord(id: 1, studentId: 1);
            seed.Context.ConsentRecords.Add(consent);
            seed.Context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
            {
                Id = 1,
                ConsentRecordId = consent.Id,
                EmergencyContact = "Someone, 07000 000000"
            });
            seed.Context.SaveChanges();
        }

        using var teacher = CreateContext(Role.Teacher);

        var consentRecord = Assert.Single(teacher.Context.ConsentRecords.Where(c => c.StudentId == 1).ToList());
        Assert.Equal(ConsentStatus.Approved, consentRecord.Status);
        Assert.Equal(expiry, consentRecord.ExpiryDate);

        // The regression this whole entity split exists to prevent: sensitive details must
        // still be invisible to Teacher even though the sibling ConsentRecord row is readable.
        Assert.Empty(teacher.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void MaterializingDbsCheck_AsAdmin_WritesAuditLogEntry_WithCorrectEntityId()
    {
        using (var seed = CreateContext(Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 42, teacherUserId: 10));
            seed.Context.SaveChanges();
        }

        using (var admin = CreateContext(Role.Admin))
        {
            var dbsCheck = admin.Context.DbsChecks.Single();
            Assert.Equal(42, dbsCheck.Id);
        }

        using var verify = CreateContext(Role.Admin);
        var logEntry = Assert.Single(verify.Context.SensitiveDataAccessLogs.ToList());

        Assert.Equal(nameof(DbsCheck), logEntry.Entity);
        Assert.Equal(42, logEntry.EntityId);
        Assert.Equal(SensitiveAccessType.View, logEntry.AccessType);
    }

    [Fact]
    public void MultiRowRead_OfTwoDbsChecks_WritesOneAuditLogEntryPerRow()
    {
        using (var seed = CreateContext(Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 2, teacherUserId: 10));
            seed.Context.SaveChanges();
        }

        using (var admin = CreateContext(Role.Admin))
        {
            var results = admin.Context.DbsChecks.OrderBy(d => d.Id).ToList();
            Assert.Equal(2, results.Count);
        }

        using var verify = CreateContext(Role.Admin);
        var logEntries = verify.Context.SensitiveDataAccessLogs
            .Where(l => l.Entity == nameof(DbsCheck))
            .OrderBy(l => l.EntityId)
            .ToList();

        Assert.Equal(2, logEntries.Count);
        Assert.Equal(1, logEntries[0].EntityId);
        Assert.Equal(2, logEntries[1].EntityId);
    }
}
