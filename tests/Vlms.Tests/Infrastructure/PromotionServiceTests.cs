using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Progress;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies auto-promotion (docs/design/low-level-design.md "PromotionService", STATE.md): a
/// student is promoted once every active lesson in their current rank is complete (reversed
/// completions don't count), the RankBadge for the completed rank is awarded if one is configured
/// (and promotion still proceeds if not), and the final rank graduates instead of advancing.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="LessonProposalServiceTests"/>. A fresh
/// database is created per test method (xUnit constructs a new test-class instance per [Fact]), so
/// each test seeds only what it needs without cross-test contamination.
/// </summary>
public sealed class PromotionServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public PromotionServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-promotion-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateContext();
        schema.Context.Database.EnsureCreated();

        schema.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        schema.Context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Explorer" }); // final rank
        schema.Context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "teacher-1", DisplayName = "Teacher One", Email = "teacher1@example.com" });
        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class CallerContext(ServiceProvider provider, IServiceScope scope, VlmsDbContext context) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public PromotionService Service => new(Context);

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    // PromotionService does its own no role checks (see its doc comment), so the caller identity
    // is irrelevant here — Admin throughout, purely to satisfy FakeCurrentUserContext's shape.
    private CallerContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(1, Role.Admin));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new CallerContext(provider, scope, context);
    }

    private static void SeedStudentAtRank1(VlmsDbContext context, int studentId = 100)
    {
        context.Students.Add(new Student
        {
            Id = studentId,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = studentId,
            StudentId = studentId,
            RankId = 1,
            StartedAt = DateTime.UtcNow.AddDays(-30),
            CompletedAt = null
        });
        context.SaveChanges();
    }

    private static void Complete(VlmsDbContext context, int id, int studentId, int lessonId, bool isReversed = false)
    {
        context.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = id,
            StudentId = studentId,
            LessonId = lessonId,
            CompletedByUserId = 1,
            CompletedAt = DateTime.UtcNow,
            IsReversed = isReversed,
            ReversedAt = isReversed ? DateTime.UtcNow : null
        });
        context.SaveChanges();
    }

    [Fact]
    public async Task CheckAndPromoteAsync_AllActiveLessonsComplete_PromotesToNextRank()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            setup.Context.SaveChanges();
            SeedStudentAtRank1(setup.Context);
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 1);
        }

        using var caller = CreateContext();
        var promoted = await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);
        Assert.True(promoted);

        using var verify = CreateContext();
        var student = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(2, student.CurrentRankId);
        Assert.Equal(StudentStatus.Active, student.Status);

        var oldProgress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == 100 && p.RankId == 1);
        Assert.NotNull(oldProgress.CompletedAt);

        var newProgress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == 100 && p.RankId == 2);
        Assert.Null(newProgress.CompletedAt);
    }

    [Fact]
    public async Task CheckAndPromoteAsync_IncompleteActiveLessons_DoesNotPromote()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            setup.Context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "First Aid", ContentBlobKey = "blob/l2", IsActive = true });
            setup.Context.SaveChanges();
            SeedStudentAtRank1(setup.Context);
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 1); // only one of two lessons done
        }

        using var caller = CreateContext();
        var promoted = await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);
        Assert.False(promoted);

        using var verify = CreateContext();
        var student = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(1, student.CurrentRankId);

        var progress = await verify.Context.StudentRankProgresses.SingleAsync(p => p.StudentId == 100 && p.RankId == 1);
        Assert.Null(progress.CompletedAt);
    }

    [Fact]
    public async Task CheckAndPromoteAsync_ReversedCompletion_DoesNotCountTowardPromotion()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            setup.Context.SaveChanges();
            SeedStudentAtRank1(setup.Context);
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 1, isReversed: true);
        }

        using var caller = CreateContext();
        var promoted = await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);
        Assert.False(promoted);
    }

    [Fact]
    public async Task CheckAndPromoteAsync_WithRankBadgeConfigured_AwardsStudentBadge()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            setup.Context.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "blob/badge-recruit" });
            setup.Context.SaveChanges();
            SeedStudentAtRank1(setup.Context);
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 1);
        }

        using var caller = CreateContext();
        await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);

        using var verify = CreateContext();
        var badge = await verify.Context.StudentBadges.SingleAsync(b => b.StudentId == 100);
        Assert.Equal(1, badge.RankBadgeId);
    }

    [Fact]
    public async Task CheckAndPromoteAsync_WithNoRankBadgeConfigured_StillPromotes_AndAwardsNoBadge()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", ContentBlobKey = "blob/l1", IsActive = true });
            setup.Context.SaveChanges();
            SeedStudentAtRank1(setup.Context);
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 1);
        }

        using var caller = CreateContext();
        var promoted = await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);
        Assert.True(promoted);

        using var verify = CreateContext();
        Assert.Empty(verify.Context.StudentBadges.Where(b => b.StudentId == 100));
    }

    [Fact]
    public async Task CheckAndPromoteAsync_AtFinalRank_SetsGraduated_InsteadOfAdvancing()
    {
        using (var setup = CreateContext())
        {
            setup.Context.Lessons.Add(new Lesson { Id = 2, RankId = 2, Code = "L2", Title = "Leadership", ContentBlobKey = "blob/l2", IsActive = true });
            setup.Context.Students.Add(new Student
            {
                Id = 100,
                Name = "Alex Student",
                DateOfBirth = new DateOnly(2012, 1, 1),
                CurrentRankId = 2, // already at the final rank
                Status = StudentStatus.Active,
                EnrolmentDate = new DateOnly(2024, 1, 1)
            });
            setup.Context.StudentRankProgresses.Add(new StudentRankProgress
            {
                Id = 100,
                StudentId = 100,
                RankId = 2,
                StartedAt = DateTime.UtcNow.AddDays(-30),
                CompletedAt = null
            });
            setup.Context.SaveChanges();
            Complete(setup.Context, id: 1, studentId: 100, lessonId: 2);
        }

        using var caller = CreateContext();
        var promoted = await caller.Service.CheckAndPromoteAsync(studentId: 100, CancellationToken.None);
        Assert.True(promoted);

        using var verify = CreateContext();
        var student = await verify.Context.Students.SingleAsync(s => s.Id == 100);
        Assert.Equal(StudentStatus.Graduated, student.Status);
        Assert.Equal(2, student.CurrentRankId); // unchanged — no rank beyond the final one

        // No new StudentRankProgress row opened past the final rank.
        Assert.Single(verify.Context.StudentRankProgresses.Where(p => p.StudentId == 100));
    }
}
