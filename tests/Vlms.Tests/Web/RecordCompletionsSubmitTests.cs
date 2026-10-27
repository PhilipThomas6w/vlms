using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Attendance;
using Vlms.Infrastructure.Progress;
using Vlms.Tests.Infrastructure;
using Vlms.Web.Components.Pages.Progress;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Behavioural coverage (bUnit) for the double-submit guard on Progress/RecordCompletions.razor: a second
/// click on "Record completions" while the first batch is still running must not start a second batch,
/// and the button must show its busy state meanwhile.
///
/// The real page, CompletionService, AttendanceService and VlmsDbContext (SQLite in memory) are used. Timing
/// is controlled by an EF Core SaveChanges interceptor that holds the first completion save until the test
/// releases it, so "the first submit is still in progress" is a deterministic state, not a race.
/// </summary>
public sealed class RecordCompletionsSubmitTests : BunitContext, IDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;
    private readonly HoldFirstCompletionSave _hold = new();

    public RecordCompletionsSubmitTests()
    {
        _connectionString = $"Data Source=file:vlms-record-completions-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using (var seed = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin)))
        {
            seed.Database.EnsureCreated();
            Seed(seed);
        }

        // The page focuses its results panel after a submit (ElementReference.FocusAsync).
        JSInterop.Mode = JSRuntimeMode.Loose;

        // The page requires the Teacher role (policy RequireTeacher); the service also checks it itself.
        var auth = AddAuthorization();
        auth.SetAuthorized("Teacher One");
        auth.SetRoles("Teacher");

        Services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(10, Role.Teacher));
        Services.AddSingleton<IBlobStorage>(new InMemoryBlobStorage());
        Services.AddSingleton(_hold);
        Services.AddDbContext<VlmsDbContext>((sp, options) => options
            .UseSqlite(_connectionString)
            .AddInterceptors(sp.GetRequiredService<HoldFirstCompletionSave>()));
        Services.AddScoped<PromotionService>();
        Services.AddScoped<CertificateService>();
        Services.AddScoped<CompletionService>();
        Services.AddScoped<AttendanceService>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hold.Release();
            _anchor.Dispose();
        }

        base.Dispose(disposing);
    }

    [Fact]
    public async Task SecondClickWhileFirstBatchIsRunning_DoesNotStartASecondBatch_AndButtonShowsBusy()
    {
        var cut = Render<RecordCompletions>();
        SelectLessonAndBothStudents(cut);

        var first = cut.Find(".record__bar button").ClickAsync(new());

        // The first batch is now parked inside its first save.
        await _hold.FirstSaveReached.WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() =>
        {
            var button = cut.Find(".record__bar button");
            Assert.True(button.HasAttribute("disabled"));
            Assert.Contains("Recording", button.TextContent);
        });

        // A second submit while the first is in progress: the markup disables the button, and the
        // handler's own _busy guard is the backstop (the DOM is never the only defence).
        await cut.Find(".record__bar button").ClickAsync(new());
        Assert.Equal(1, _hold.CompletionSaves);

        _hold.Release();
        await first;
        cut.WaitForAssertion(() => Assert.Contains("2 completions recorded", cut.Markup));

        Assert.Equal(2, CountCompletions());
    }

    [Fact]
    public async Task SingleSubmit_SavesEverySelectedStudent_AndShowsResults()
    {
        _hold.Release();
        var cut = Render<RecordCompletions>();
        SelectLessonAndBothStudents(cut);

        await cut.Find(".record__bar button").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("2 completions recorded", cut.Markup));
        Assert.Equal(2, CountCompletions());
        Assert.DoesNotContain("Recording…", cut.Markup);
    }

    private static void SelectLessonAndBothStudents(IRenderedComponent<RecordCompletions> cut)
    {
        cut.WaitForState(() => cut.FindAll("input[name=lesson]").Count > 0);
        cut.Find("input#lesson1").Change(true);
        cut.WaitForState(() => cut.FindAll("input#student100").Count > 0);
        cut.Find("input#student100").Change(true);
        cut.Find("input#student101").Change(true);
    }

    private int CountCompletions()
    {
        using var db = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin));
        return db.StudentLessonCompletions.Count();
    }

    private static void Seed(VlmsDbContext context)
    {
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher One", Email = "teacher1@example.com" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots 101", Order = 1, IsActive = true });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "jane@example.com", IsPrimary = true });
        foreach (var (id, name) in new[] { (100, "Alex Student"), (101, "Blake Student") })
        {
            context.Students.Add(new Student
            {
                Id = id,
                Name = name,
                DateOfBirth = new DateOnly(2012, 1, 1),
                CurrentRankId = 1,
                Status = StudentStatus.Active,
                EnrolmentDate = new DateOnly(2024, 1, 1)
            });
            context.StudentRankProgresses.Add(new StudentRankProgress
            {
                Id = id,
                StudentId = id,
                RankId = 1,
                StartedAt = DateTime.UtcNow.AddDays(-30)
            });
            context.ConsentRecords.Add(new ConsentRecord
            {
                Id = id,
                StudentId = id,
                PeriodStart = new DateOnly(2026, 1, 1),
                PeriodEnd = new DateOnly(2036, 12, 31),
                PhotoMediaConsent = true,
                TransportConsent = true,
                OffsiteTripConsent = true,
                DataSharingConsent = true,
                Status = ConsentStatus.Approved,
                SubmittedByParentId = 1,
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))
            });
        }

        context.SaveChanges();
    }

    /// <summary>
    /// Holds the first save that inserts a StudentLessonCompletion until <see cref="Release"/> is called,
    /// and counts the saves that insert completions.
    /// </summary>
    private sealed class HoldFirstCompletionSave : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saves;

        public Task FirstSaveReached => _reached.Task;
        public int CompletionSaves => Volatile.Read(ref _saves);

        public void Release() => _gate.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var inserts = eventData.Context!.ChangeTracker.Entries<StudentLessonCompletion>()
                .Any(e => e.State == EntityState.Added);
            if (inserts && Interlocked.Increment(ref _saves) == 1)
            {
                _reached.TrySetResult();
                await _gate.Task;
            }

            return result;
        }
    }
}
