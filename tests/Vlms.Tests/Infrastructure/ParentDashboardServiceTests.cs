using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Engagement;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="ParentDashboardService"/> (STATE.md, functional.md "Parent engagement",
/// quality/test-plan.md TC-006: "Parent views own child's dashboard; cannot view another parent's
/// child"). The core property under test is scoping: a Parent sees only students linked to them via
/// <see cref="StudentGuardianLink"/> -&gt; <see cref="ParentGuardian.AppUserId"/> (the same
/// relationship <see cref="Vlms.Infrastructure.Authorization.ParentStudentAccessHandler"/> checks,
/// via the shared <see cref="Vlms.Infrastructure.Authorization.ParentGuardianLinkage"/> helper) —
/// never another parent's child, even though both students exist in the same database.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="StudentRegistrationServiceTests"/>.
/// </summary>
public sealed class ParentDashboardServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public ParentDashboardServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-parentdash-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext(new FakeCurrentUserContext(userId: 1, Role.Parent));
        schema.Context.Database.EnsureCreated();

        schema.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        schema.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", ContentBlobKey = "blob/l1", IsActive = true });
        schema.Context.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "badges/recruit.png" });
        schema.Context.AppUsers.Add(new AppUser { Id = 10, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });

        // Parent A (AppUserId 1) is linked to Alex (Student 100) only.
        schema.Context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "parent-a", DisplayName = "Parent A", Email = "parenta@example.com" });
        schema.Context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Parent A", ContactInfo = "parenta@example.com", IsPrimary = true, AppUserId = 1 });
        schema.Context.Students.Add(new Student
        {
            Id = 100, Name = "Alex", DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        schema.Context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 1, CreatedByUserId = 10 });
        schema.Context.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = 1, StudentId = 100, RankId = 1, StartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CompletedAt = null
        });
        schema.Context.StudentBadges.Add(new StudentBadge { Id = 1, StudentId = 100, RankBadgeId = 1, AwardedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) });
        schema.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = 10,
            CompletedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = false
        });
        schema.Context.Certificates.Add(new Certificate { Id = 1, StudentLessonCompletionId = 1, GeneratedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), BlobKey = "certificates/100/1.pdf" });
        schema.Context.ConsentRecords.Add(new ConsentRecord
        {
            Id = 1, StudentId = 100, PeriodStart = new DateOnly(2025, 1, 1), PeriodEnd = new DateOnly(2026, 1, 1),
            PhotoMediaConsent = true, TransportOffsiteConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = 1, ExpiryDate = new DateOnly(2026, 12, 31)
        });

        // Parent B (AppUserId 2) is linked to Sam (Student 200) only — a completely separate family.
        schema.Context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "parent-b", DisplayName = "Parent B", Email = "parentb@example.com" });
        schema.Context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Parent B", ContactInfo = "parentb@example.com", IsPrimary = true, AppUserId = 2 });
        schema.Context.Students.Add(new Student
        {
            Id = 200, Name = "Sam", DateOfBirth = new DateOnly(2013, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        schema.Context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 200, ParentGuardianId = 2, CreatedByUserId = 10 });

        // Parent C (AppUserId 3) has no linked students at all.
        schema.Context.AppUsers.Add(new AppUser { Id = 3, EntraObjectId = "parent-c", DisplayName = "Parent C", Email = "parentc@example.com" });
        schema.Context.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Parent C", ContactInfo = "parentc@example.com", IsPrimary = true, AppUserId = 3 });

        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ParentDashboardService Service(ICurrentUserContext currentUser) => new(Context, currentUser);

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerContext CreateContext(ICurrentUserContext currentUser)
    {
        var services = new ServiceCollection();
        services.AddSingleton(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context);
    }

    [Fact]
    public async Task GetDashboardAsync_ParentA_SeesOnlyAlex_WithFullProgressBadgesCertificatesAndConsent()
    {
        var parentA = new FakeCurrentUserContext(userId: 1, Role.Parent);
        using var run = CreateContext(parentA);

        var result = await run.Service(parentA).GetDashboardAsync();

        var alex = Assert.Single(result);
        Assert.Equal(100, alex.StudentId);
        Assert.Equal("Alex", alex.StudentName);
        Assert.Equal("Recruit", alex.CurrentRankName);
        Assert.NotNull(alex.CurrentRankStartedAt);
        Assert.Single(alex.Badges, b => b.RankName == "Recruit");
        Assert.Single(alex.Certificates, c => c.LessonTitle == "Knots 101");
        Assert.Equal(ConsentStatus.Approved, alex.ConsentStatus);
        Assert.Equal(new DateOnly(2026, 12, 31), alex.ConsentExpiryDate);
    }

    /// <summary>
    /// The property TC-006 names explicitly: Parent B must never see Alex (Parent A's child), even
    /// though Alex exists in the same database Parent B's own query runs against.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_ParentB_SeesOnlySam_NeverAlex_CrossParentAccessIsDenied()
    {
        var parentB = new FakeCurrentUserContext(userId: 2, Role.Parent);
        using var run = CreateContext(parentB);

        var result = await run.Service(parentB).GetDashboardAsync();

        var sam = Assert.Single(result);
        Assert.Equal(200, sam.StudentId);
        Assert.Equal("Sam", sam.StudentName);
        Assert.DoesNotContain(result, s => s.StudentId == 100);
        Assert.DoesNotContain(result, s => s.StudentName == "Alex");
    }

    [Fact]
    public async Task GetDashboardAsync_ParentWithNoLinkedStudents_ReturnsEmpty()
    {
        var parentC = new FakeCurrentUserContext(userId: 3, Role.Parent);
        using var run = CreateContext(parentC);

        var result = await run.Service(parentC).GetDashboardAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDashboardAsync_SamHasNoConsentRecordYet_ReturnsNullConsentFields()
    {
        var parentB = new FakeCurrentUserContext(userId: 2, Role.Parent);
        using var run = CreateContext(parentB);

        var result = await run.Service(parentB).GetDashboardAsync();

        var sam = Assert.Single(result);
        Assert.Null(sam.ConsentStatus);
        Assert.Null(sam.ConsentExpiryDate);
        Assert.Empty(sam.Badges);
        Assert.Empty(sam.Certificates);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.Approver)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Student)]
    public async Task GetDashboardAsync_ByNonParentRole_Throws(Role role)
    {
        var caller = new FakeCurrentUserContext(userId: 1, role);
        using var run = CreateContext(caller);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetDashboardAsync());
    }
}
