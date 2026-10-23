using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
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
/// how separate connections/contexts behave against the real self-hosted PostgreSQL target in
/// production (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md; this wording predates that
/// pivot and named Azure SQL, which is no longer this project's database engine).
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

    private CallerContext CreateContext(params Role[] roles) => CreateContext(hasMfaEnabled: true, roles);

    private CallerContext CreateContext(bool hasMfaEnabled, params Role[] roles) =>
        CreateContextForUser(userId: 1, hasMfaEnabled, roles);

    private CallerContext CreateContextForUser(int? userId, params Role[] roles) =>
        CreateContextForUser(userId, hasMfaEnabled: true, roles);

    private CallerContext CreateContextForUser(int? userId, bool hasMfaEnabled, params Role[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(userId, hasMfaEnabled, roles));
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
        TransportConsent = true, OffsiteTripConsent = true,
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
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Parent One", Email = "parent1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 2, DisplayName = "Admin One", Email = "admin1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
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

    // Amended by adr/0006 (FR-015): this test used to assert Teacher could read neither entity.
    // Teacher may now read ConsentSensitiveDetails; DbsCheck stays invisible to Teacher.
    [Fact]
    public void Teacher_CannotRead_DbsCheck_ButCanRead_ConsentSensitiveDetails()
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
        Assert.Single(teacher.Context.ConsentSensitiveDetails.ToList());
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

        // Amended by adr/0006 (FR-015): this used to assert the sensitive details were invisible
        // to Teacher. Teacher now reads them (read only); the entity split still matters for the
        // Approver role and for unlinked Parents — see the parent/Approver tests below.
        Assert.Single(teacher.Context.ConsentSensitiveDetails.ToList());
    }

    // quality/test-plan.md TC-008: the Approver role is curriculum-only, so it gets no more
    // safeguarding visibility than a Teacher, even with MFA enabled. ConsentRecord status/expiry
    // stays readable (security-compliance.md); approving consent is ConsentRecordServiceTests'
    // DecideAsync_ByApprover_Throws.
    [Fact]
    public void Approver_CannotRead_DbsCheck_Or_ConsentSensitiveDetails_ButCanRead_ConsentRecord()
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

        using var approver = CreateContext(hasMfaEnabled: true, Role.Approver);

        Assert.Empty(approver.Context.DbsChecks.ToList());
        Assert.Empty(approver.Context.ConsentSensitiveDetails.ToList());
        Assert.Single(approver.Context.ConsentRecords.Where(c => c.StudentId == 1).ToList());
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

    // --- MFA-gated roles (adr/0004 §"MFA-gated roles", pass 7 — STATE.md "Identity" item 4): the
    // query filter now requires HasMfaEnabled in addition to Admin/SafeguardingOfficer. ------------

    [Fact]
    public void Admin_WithoutMfaEnabled_CannotRead_DbsCheck_Or_ConsentSensitiveDetails()
    {
        using (var seed = CreateContext(hasMfaEnabled: true, Role.Admin))
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

        // Same role as Admin_CanRead_DbsCheck_And_ConsentSensitiveDetails above — only HasMfaEnabled
        // differs — proving the denial is specifically about MFA, not role.
        using var adminWithoutMfa = CreateContext(hasMfaEnabled: false, Role.Admin);

        Assert.Empty(adminWithoutMfa.Context.DbsChecks.ToList());
        Assert.Empty(adminWithoutMfa.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void SafeguardingOfficer_WithoutMfaEnabled_CannotRead_DbsCheck_Or_ConsentSensitiveDetails()
    {
        using (var seed = CreateContext(hasMfaEnabled: true, Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));
            seed.Context.SaveChanges();
        }

        using var sgoWithoutMfa = CreateContext(hasMfaEnabled: false, Role.SafeguardingOfficer);

        Assert.Empty(sgoWithoutMfa.Context.DbsChecks.ToList());
    }

    [Fact]
    public void SafeguardingOfficer_WithMfaEnabled_CanRead_DbsCheck()
    {
        // Confirms the previous test's denial is genuinely about MFA, not an accidental regression
        // of SafeguardingOfficer's own role-based access (which the pre-existing
        // Admin_CanRead_DbsCheck_And_ConsentSensitiveDetails test above doesn't cover, since it only
        // ever uses Role.Admin).
        using (var seed = CreateContext(hasMfaEnabled: true, Role.Admin))
        {
            SeedCoreReferenceData(seed.Context);
            seed.Context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));
            seed.Context.SaveChanges();
        }

        using var sgoWithMfa = CreateContext(hasMfaEnabled: true, Role.SafeguardingOfficer);

        Assert.Single(sgoWithMfa.Context.DbsChecks.ToList());
    }

    // --- adr/0006 (functional.md FR-015): Teacher read, linked-Parent read, everyone else denied.

    // Two families: Parent One (AppUser 1, guardian 1) is linked to student 1 only; Parent Two
    // (AppUser 3, guardian 2) to student 2 only. Guardian 3 has no account yet (AppUserId null) and
    // is linked to student 1 — the case the filter's "UserId != null" conjunct exists for. Each
    // student has one consent record with sensitive details, and Teacher One has a DbsCheck.
    private void SeedTwoFamilies()
    {
        using var seed = CreateContext(Role.Admin);
        var context = seed.Context;
        SeedCoreReferenceData(context);
        context.AppUsers.Add(new AppUser { Id = 3, DisplayName = "Parent Two", Email = "parent2@example.com" });
        context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Parent Two", ContactInfo = "parent2@example.com", IsPrimary = true, AppUserId = 3 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "No Account", ContactInfo = "none@example.com", IsPrimary = false });
        context.Students.Add(new Student
        {
            Id = 2,
            Name = "Sam Student",
            DateOfBirth = new DateOnly(2016, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.SaveChanges();

        // ParentGuardian 1 is seeded without an account by SeedCoreReferenceData; link it to
        // AppUser 1 the same way the guardian-invite flow would.
        var guardianOne = context.ParentGuardians.Single(p => p.Id == 1);
        context.Entry(guardianOne).CurrentValues[nameof(ParentGuardian.AppUserId)] = 1;

        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 1, ParentGuardianId = 1, CreatedByUserId = 2 });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 2, ParentGuardianId = 2, CreatedByUserId = 2 });
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 1, ParentGuardianId = 3, CreatedByUserId = 2 });

        context.ConsentRecords.Add(NewConsentRecord(id: 1, studentId: 1));
        context.ConsentRecords.Add(NewConsentRecord(id: 2, studentId: 2));
        context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 1, ConsentRecordId = 1, EmergencyContact = "Family One" });
        context.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 2, ConsentRecordId = 2, EmergencyContact = "Family Two" });
        context.DbsChecks.Add(NewDbsCheck(id: 1, teacherUserId: 10));
        context.SaveChanges();
    }

    [Fact]
    public void LinkedParent_CanRead_OwnChildsConsentSensitiveDetails_Only()
    {
        SeedTwoFamilies();

        using var parentOne = CreateContextForUser(1, Role.Parent);
        var visible = Assert.Single(parentOne.Context.ConsentSensitiveDetails.ToList());
        Assert.Equal(1, visible.ConsentRecordId);
        Assert.Equal("Family One", visible.EmergencyContact);

        using var parentTwo = CreateContextForUser(3, Role.Parent);
        var visibleToTwo = Assert.Single(parentTwo.Context.ConsentSensitiveDetails.ToList());
        Assert.Equal(2, visibleToTwo.ConsentRecordId);
    }

    [Fact]
    public void Parent_CannotRead_AnotherChildsDetails_EvenByExplicitId()
    {
        SeedTwoFamilies();

        using var parentOne = CreateContextForUser(1, Role.Parent);

        Assert.Null(parentOne.Context.ConsentSensitiveDetails.SingleOrDefault(d => d.ConsentRecordId == 2));
        Assert.Null(parentOne.Context.ConsentRecords
            .Where(c => c.Id == 2)
            .Select(c => c.SensitiveDetails)
            .SingleOrDefault());
    }

    [Fact]
    public void ParentRole_WithNoResolvedUserId_ReadsNothing_EvenWhereAGuardianHasNoAccount()
    {
        SeedTwoFamilies();

        using var unresolvedParent = CreateContextForUser(null, Role.Parent);

        Assert.Empty(unresolvedParent.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void ParentWhoHasLeft_ReadsNothing()
    {
        SeedTwoFamilies();
        using (var admin = CreateContext(Role.Admin))
        {
            var parentOne = admin.Context.AppUsers.Single(u => u.Id == 1);
            admin.Context.Entry(parentOne).CurrentValues[nameof(AppUser.HasLeft)] = true;
            admin.Context.SaveChanges();
        }

        using var leftParent = CreateContextForUser(1, Role.Parent);

        Assert.Empty(leftParent.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void UnlinkedParent_ReadsNothing()
    {
        SeedTwoFamilies();

        // AppUser 2 holds the Parent role here but has no ParentGuardian row at all.
        using var unlinked = CreateContextForUser(2, Role.Parent);

        Assert.Empty(unlinked.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void Parent_And_Approver_CannotRead_DbsCheck()
    {
        SeedTwoFamilies();

        using var parent = CreateContextForUser(1, Role.Parent);
        Assert.Empty(parent.Context.DbsChecks.ToList());

        using var approver = CreateContextForUser(20, Role.Approver);
        Assert.Empty(approver.Context.DbsChecks.ToList());
        Assert.Empty(approver.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void Teacher_WithMfa_CanRead_EveryStudentsDetails_ButNeverDbsCheck()
    {
        SeedTwoFamilies();

        using var teacher = CreateContextForUser(10, hasMfaEnabled: true, Role.Teacher);

        Assert.Equal(2, teacher.Context.ConsentSensitiveDetails.ToList().Count);
        Assert.Empty(teacher.Context.DbsChecks.ToList());
    }

    [Fact]
    public void Teacher_WithoutMfa_CannotRead_AnyDetails()
    {
        SeedTwoFamilies();

        // adr/0006 amendment (Philip, 22/10/2026): the Teacher arm of the ConsentSensitiveDetails
        // filter requires MFA, matching MfaRequirementHandler.
        using var teacher = CreateContextForUser(10, hasMfaEnabled: false, Role.Teacher);

        Assert.Empty(teacher.Context.ConsentSensitiveDetails.ToList());
        Assert.Empty(teacher.Context.DbsChecks.ToList());
    }

    [Fact]
    public void Parent_WithoutMfa_StillReads_OwnLinkedChildsDetails()
    {
        SeedTwoFamilies();

        // Parents are not MFA-gated (MFA offered, never required).
        using var parent = CreateContextForUser(3, hasMfaEnabled: false, Role.Parent);

        Assert.Single(parent.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void SafeguardingOfficer_WithMfa_StillReadsAllDetails()
    {
        SeedTwoFamilies();

        using var sgo = CreateContextForUser(5, Role.SafeguardingOfficer);

        Assert.Equal(2, sgo.Context.ConsentSensitiveDetails.ToList().Count);
        Assert.Single(sgo.Context.DbsChecks.ToList());
    }

    [Fact]
    public void TeacherRead_WritesOneAuditRowPerDetailsRow_AttributedToTheTeacher()
    {
        SeedTwoFamilies();

        using (var teacher = CreateContextForUser(10, Role.Teacher))
        {
            Assert.Equal(2, teacher.Context.ConsentSensitiveDetails.ToList().Count);
        }

        using var verify = CreateContext(Role.Admin);
        var logEntries = verify.Context.SensitiveDataAccessLogs
            .Where(l => l.Entity == nameof(ConsentSensitiveDetails))
            .OrderBy(l => l.EntityId)
            .ToList();

        Assert.Equal(2, logEntries.Count);
        Assert.All(logEntries, l => Assert.Equal(10, l.UserId));
        Assert.Equal([1, 2], logEntries.Select(l => l.EntityId));
    }

    [Fact]
    public void ParentRead_WritesAuditRow_AttributedToTheParent_AndNoneForHiddenRows()
    {
        SeedTwoFamilies();

        using (var parent = CreateContextForUser(3, Role.Parent))
        {
            Assert.Single(parent.Context.ConsentSensitiveDetails.ToList());
        }

        using var verify = CreateContext(Role.Admin);
        var logEntry = Assert.Single(verify.Context.SensitiveDataAccessLogs
            .Where(l => l.Entity == nameof(ConsentSensitiveDetails))
            .ToList());

        Assert.Equal(3, logEntry.UserId);
        Assert.Equal(2, logEntry.EntityId);
    }

    // --- FR-017 (clarified 23/10/2026): a staff leaver's staff roles stop counting, through the
    // real IdentityCurrentUserContext the app uses, so the Teacher/Admin/SO routes of the
    // query filters refuse them; a staff leaver who is also a parent keeps the parent route. ---

    /// <summary>A caller resolved by the real <see cref="IdentityCurrentUserContext"/> from a principal.</summary>
    private CallerContext CreateRealContextForUser(int userId)
    {
        var options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"));
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new IdentityCurrentUserContext(principal, options));
        services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new CallerContext(provider, scope, scope.ServiceProvider.GetRequiredService<VlmsDbContext>());
    }

    /// <summary>AppUser 1 (Parent One, guardian of student 1) is also a Teacher, Admin and SO here, all with MFA.</summary>
    private void SeedStaffParent()
    {
        SeedTwoFamilies();
        using var admin = CreateContext(Role.Admin);
        admin.Context.Users.Add(new VlmsIdentityUser { Id = 1, UserName = "parent1@example.com", Email = "parent1@example.com", TwoFactorEnabled = true });
        admin.Context.Users.Add(new VlmsIdentityUser { Id = 10, UserName = "teacher1@example.com", Email = "teacher1@example.com", TwoFactorEnabled = true });
        foreach (var role in new[] { Role.Parent, Role.Teacher, Role.Admin, Role.SafeguardingOfficer })
        {
            admin.Context.UserRoles.Add(new UserRole { UserId = 1, Role = role });
        }

        admin.Context.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher });
        admin.Context.SaveChanges();
    }

    private void MarkLeft(int userId)
    {
        using var admin = CreateContext(Role.Admin);
        var user = admin.Context.AppUsers.Single(u => u.Id == userId);
        admin.Context.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
        admin.Context.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = DateTime.UtcNow;
        admin.Context.SaveChanges();
    }

    [Fact]
    public void StaffParent_BeforeLeaving_ReadsEveryFamilysDetailsAndDbs_ThroughTheStaffRoutes()
    {
        SeedStaffParent();

        using var caller = CreateRealContextForUser(1);

        Assert.Equal(2, caller.Context.ConsentSensitiveDetails.Count());
        Assert.Single(caller.Context.DbsChecks.ToList());
    }

    [Fact]
    public void StaffParentWhoHasLeft_ReadsOnlyTheirOwnChildsDetails_AndNoDbs()
    {
        SeedStaffParent();
        MarkLeft(1);

        using var caller = CreateRealContextForUser(1);

        var details = caller.Context.ConsentSensitiveDetails.ToList();
        Assert.Equal("Family One", Assert.Single(details).EmergencyContact);
        Assert.Empty(caller.Context.DbsChecks.ToList());
    }

    [Fact]
    public void TeacherWhoHasLeft_ReadsNoConsentDetails_ThroughTheTeacherRoute()
    {
        SeedStaffParent();
        using (var before = CreateRealContextForUser(10))
        {
            Assert.Equal(2, before.Context.ConsentSensitiveDetails.Count());
        }

        MarkLeft(10);

        using var caller = CreateRealContextForUser(10);
        Assert.Empty(caller.Context.ConsentSensitiveDetails.ToList());
    }

    [Fact]
    public void StaffParentWhoHasLeft_HoldsParentButNoStaffRole()
    {
        SeedStaffParent();
        MarkLeft(1);
        var options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "TestAuth"));

        var sut = new IdentityCurrentUserContext(principal, options);

        Assert.True(sut.HasRole(Role.Parent));
        Assert.False(sut.HasRole(Role.Teacher));
        Assert.False(sut.HasRole(Role.Admin));
        Assert.False(sut.HasRole(Role.SafeguardingOfficer));
        using var db = new VlmsDbContext(options, NullCurrentUserContext.Instance);
        Assert.False(LeftStaffPolicy.IsLockedOut(db, 1));
        Assert.False(LeftStaffPolicy.StaffRoleCounts(db, 1));
    }
}
