using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Progress;
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
        schema.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", Order = 1, IsActive = true });
        schema.Context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "Fire Safety", Order = 1, IsActive = true });
        schema.Context.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "badges/recruit.png" });
        schema.Context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });

        // Parent A (AppUserId 1) is linked to Alex (Student 100) only.
        schema.Context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Parent A", Email = "parenta@example.com" });
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

        // Alex also has a reversed completion (Teacher self-correction of a data-entry mistake) whose
        // certificate was already generated before the reversal. Philip's decision, 2026-07-20
        // (STATE.md): a certificate generated from a reversed completion is itself wrong and must be
        // hidden from the parent dashboard (see ParentDashboardService's certificate-join comment) — so
        // this certificate ("Fire Safety") is expected to be absent from the dashboard.
        schema.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = 2, StudentId = 100, LessonId = 2, CompletedByUserId = 10,
            CompletedAt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc), IsReversed = true, ReversedAt = new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc)
        });
        schema.Context.Certificates.Add(new Certificate { Id = 2, StudentLessonCompletionId = 2, GeneratedAt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc), BlobKey = "certificates/100/2.pdf" });

        schema.Context.ConsentRecords.Add(new ConsentRecord
        {
            Id = 1, StudentId = 100, PeriodStart = new DateOnly(2025, 1, 1), PeriodEnd = new DateOnly(2026, 1, 1),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = 1, ExpiryDate = new DateOnly(2026, 12, 31)
        });

        // Parent B (AppUserId 2) is linked to Sam (Student 200) only — a completely separate family.
        schema.Context.AppUsers.Add(new AppUser { Id = 2, DisplayName = "Parent B", Email = "parentb@example.com" });
        schema.Context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Parent B", ContactInfo = "parentb@example.com", IsPrimary = true, AppUserId = 2 });
        schema.Context.Students.Add(new Student
        {
            Id = 200, Name = "Sam", DateOfBirth = new DateOnly(2013, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        schema.Context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 200, ParentGuardianId = 2, CreatedByUserId = 10 });

        // Parent C (AppUserId 3) has no linked students at all.
        schema.Context.AppUsers.Add(new AppUser { Id = 3, DisplayName = "Parent C", Email = "parentc@example.com" });
        schema.Context.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Parent C", ContactInfo = "parentc@example.com", IsPrimary = true, AppUserId = 3 });

        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;
        public ParentDashboardService Service(ICurrentUserContext currentUser) => new(Context, currentUser, new InMemoryBlobStorage());

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
        var certificate = Assert.Single(alex.Certificates);
        Assert.Equal("Knots 101", certificate.LessonTitle);
        Assert.Equal(ConsentStatus.Approved, alex.ConsentStatus);
        Assert.Equal(new DateOnly(2026, 12, 31), alex.ConsentExpiryDate);
    }

    /// <summary>
    /// Philip's decision, 2026-07-20 (STATE.md): a Certificate must be hidden from a parent's dashboard
    /// when the StudentLessonCompletion it was generated from has IsReversed == true — reversal means
    /// "this shouldn't have happened", so a certificate generated from a reversed completion is itself
    /// wrong and must not be shown to a parent. This reverses the earlier "no gap found" conclusion
    /// (commit 922cc69) that the previous version of this test locked in (both certificates present).
    /// See ParentDashboardService's certificate-join comment for the full reasoning.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_CertificateForReversedCompletion_IsHidden_ReversalCascadesToCertificateVisibility()
    {
        var parentA = new FakeCurrentUserContext(userId: 1, Role.Parent);
        using var run = CreateContext(parentA);

        var result = await run.Service(parentA).GetDashboardAsync();

        var alex = Assert.Single(result);
        var certificate = Assert.Single(alex.Certificates);
        Assert.Equal("Knots 101", certificate.LessonTitle);
        Assert.DoesNotContain(alex.Certificates, c => c.LessonTitle == "Fire Safety");
    }

    /// <summary>
    /// The property that actually matters (STATE.md's own framing for the reversal-flow item): proves
    /// a certificate is hidden after its underlying completion is reversed through the real
    /// <see cref="CompletionService.ReverseAsync"/> write path — not test seed data setting
    /// <see cref="StudentLessonCompletion.IsReversed"/> directly, which is all the fixture above and
    /// the test right below it do. Seeds a fresh, non-reversed completion/certificate for Alex, checks
    /// it starts visible, reverses it as the recording Teacher, then checks it disappears.
    /// </summary>
    [Fact]
    public async Task GetDashboardAsync_CertificateHiddenAfterGenuineReversal_ViaCompletionServiceReverseAsync()
    {
        int completionId;
        using (var seed = CreateContext(new FakeCurrentUserContext(userId: 1, Role.Admin)))
        {
            completionId = 3;
            seed.Context.StudentLessonCompletions.Add(new StudentLessonCompletion
            {
                Id = completionId, StudentId = 100, LessonId = 1, CompletedByUserId = 10,
                CompletedAt = DateTime.UtcNow.AddDays(-1), IsReversed = false
            });
            seed.Context.Certificates.Add(new Certificate
            {
                Id = 3, StudentLessonCompletionId = completionId, GeneratedAt = DateTime.UtcNow.AddDays(-1), BlobKey = "certificates/100/3.pdf"
            });
            seed.Context.SaveChanges();
        }

        var parentA = new FakeCurrentUserContext(userId: 1, Role.Parent);

        using (var before = CreateContext(parentA))
        {
            var alexBefore = Assert.Single(await before.Service(parentA).GetDashboardAsync());
            Assert.Contains(alexBefore.Certificates, c => c.BlobKey == "certificates/100/3.pdf");
        }

        using (var reverse = CreateContext(new FakeCurrentUserContext(userId: 10, Role.Teacher)))
        {
            var completionService = new CompletionService(
                reverse.Context,
                new FakeCurrentUserContext(userId: 10, Role.Teacher),
                new PromotionService(reverse.Context),
                new CertificateService(reverse.Context, new InMemoryBlobStorage()));

            await completionService.ReverseAsync(completionId, note: "Recorded against the wrong lesson", CancellationToken.None);
        }

        using var after = CreateContext(parentA);
        var alexAfter = Assert.Single(await after.Service(parentA).GetDashboardAsync());
        Assert.DoesNotContain(alexAfter.Certificates, c => c.BlobKey == "certificates/100/3.pdf");
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

    [Fact]
    public async Task GetJourneysAsync_ParentA_GetsAlexsJourneyOnly_LeavingOutReversedCompletions()
    {
        var parentA = new FakeCurrentUserContext(userId: 1, Role.Parent);
        using var run = CreateContext(parentA);

        var journeys = await run.Service(parentA).GetJourneysAsync();

        var (studentId, journey) = Assert.Single(journeys);
        Assert.Equal(100, studentId);
        var recruit = Assert.Single(journey.Ranks);
        Assert.Equal(JourneyRankState.Current, recruit.State);
        Assert.Equal(1, recruit.CompletedCount);
        Assert.Equal(2, recruit.Lessons.Count);
        Assert.NotNull(recruit.BadgeAwardedAt);
    }

    [Fact]
    public async Task GetJourneysAsync_ParentB_NeverGetsAlex()
    {
        var parentB = new FakeCurrentUserContext(userId: 2, Role.Parent);
        using var run = CreateContext(parentB);

        var journeys = await run.Service(parentB).GetJourneysAsync();

        Assert.Equal([200], journeys.Keys);
    }

    [Fact]
    public async Task GetJourneysAsync_ParentWithNoLinkedStudents_ReturnsEmpty()
    {
        var parentC = new FakeCurrentUserContext(userId: 3, Role.Parent);
        using var run = CreateContext(parentC);

        Assert.Empty(await run.Service(parentC).GetJourneysAsync());
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.Approver)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Student)]
    public async Task GetJourneysAsync_ByNonParentRole_Throws(Role role)
    {
        var caller = new FakeCurrentUserContext(userId: 1, role);
        using var run = CreateContext(caller);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetJourneysAsync());
    }

    [Fact]
    public async Task GetAttendanceAsync_ParentA_GetsAlexsMarksOnly()
    {
        var meeting = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);
        using (var seed = CreateContext(TestSeedUserContext.Instance))
        {
            seed.Context.ChapterEvents.Add(new ChapterEvent { Id = 1, Date = meeting, Title = "Chapter meeting", CreatedByUserId = 10, CreatedAt = DateTime.UtcNow });
            seed.Context.AttendanceRegisters.Add(new AttendanceRegister { Id = 1, ChapterEventId = 1, RankId = 1, MeetingDate = meeting, TakenByUserId = 10, TakenAt = DateTime.UtcNow });
            seed.Context.AttendanceMarks.Add(new AttendanceMark { Id = 1, AttendanceRegisterId = 1, StudentId = 100, MeetingDate = meeting, Present = true });
            seed.Context.AttendanceMarks.Add(new AttendanceMark { Id = 2, AttendanceRegisterId = 1, StudentId = 200, MeetingDate = meeting, Present = false });
            seed.Context.SaveChanges();
        }

        var parentA = new FakeCurrentUserContext(userId: 1, Role.Parent);
        using var run = CreateContext(parentA);
        var attendance = await run.Service(parentA).GetAttendanceAsync();

        var (studentId, history) = Assert.Single(attendance);
        Assert.Equal(100, studentId);
        Assert.Equal([new Vlms.Infrastructure.Attendance.AttendanceEntry(meeting, true)], history.Entries);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.Student)]
    public async Task GetAttendanceAsync_ByNonParentRole_Throws(Role role)
    {
        var caller = new FakeCurrentUserContext(userId: 1, role);
        using var run = CreateContext(caller);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => run.Service(caller).GetAttendanceAsync());
    }
}
