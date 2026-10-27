using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Retention;
using Vlms.Tests.Infrastructure;
using Vlms.Web.Components.Pages.Retention;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// The leaving and stepping-down page (functional.md FR-017 as amended 27/10/2026): stepping down
/// and reinstating are each confirmed in the page, and the history shows who acted. The real page,
/// service and SQLite database are used.
/// </summary>
public sealed class StaffSteppingDownPageTests : BunitContext, IDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public StaffSteppingDownPageTests()
    {
        _connectionString = $"Data Source=file:vlms-step-down-page-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using (var seed = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance))
        {
            seed.Database.EnsureCreated();
            seed.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
            seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
            seed.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Tom Teacher", Email = "tom@example.com" });
            seed.UserRoles.Add(new UserRole { UserId = 10, Role = Role.Teacher, ChapterId = Chapter.ShrewsburyId });
            seed.SaveChanges();
        }

        var auth = AddAuthorization();
        auth.SetAuthorized("Admin One");
        auth.SetRoles("Admin");

        Services.AddLogging();
        Services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(1, Role.Admin));
        Services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        Services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        Services.AddScoped<LeavingService>();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anchor.Dispose();
        }

        base.Dispose(disposing);
    }

    [Fact]
    public async Task StepDown_ThenReinstate_EachConfirmedInThePage_AndTheHistoryShowsWhoActed()
    {
        var cut = Render<MarkAsLeft>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Step down Tom Teacher']").Count == 1);

        await cut.Find("button[aria-label='Step down Tom Teacher']").ClickAsync(new());
        Assert.False(SteppedDown());
        await cut.Find("button[aria-label='Confirm: step down Tom Teacher']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Tom Teacher stepped down.", cut.Find("[role=status]").TextContent));
        Assert.True(SteppedDown());
        Assert.Contains("by Admin One", cut.Markup);

        await cut.Find("button[aria-label='Reinstate Tom Teacher']").ClickAsync(new());
        Assert.Contains("Reinstate Tom Teacher as Teacher?", cut.Find("p[role=alert]").TextContent);
        await cut.Find("button[aria-label='Confirm: reinstate Tom Teacher']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Tom Teacher reinstated.", cut.Find("[role=status]").TextContent));
        Assert.False(SteppedDown());
        Assert.Contains("reinstated", cut.Markup);
        Assert.Single(cut.FindAll("button[aria-label='Step down Tom Teacher']"));
    }

    [Fact]
    public async Task CancelReinstate_ChangesNothing()
    {
        using (var db = Db())
        {
            var user = db.AppUsers.Single(u => u.Id == 10);
            db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            db.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = DateTime.UtcNow;
            db.SaveChanges();
        }

        var cut = Render<MarkAsLeft>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Reinstate Tom Teacher']").Count == 1);
        await cut.Find("button[aria-label='Reinstate Tom Teacher']").ClickAsync(new());
        await cut.Find("button[aria-label='Cancel reinstating Tom Teacher']").ClickAsync(new());

        Assert.True(SteppedDown());
        Assert.Empty(cut.FindAll("button[aria-label='Confirm: reinstate Tom Teacher']"));
    }

    [Fact]
    public void TheHistory_ShowsADeletedAdminAsAFormerMemberOfStaff_ButNamesNobodyForAStepDownRecordedBeforeTheHistory()
    {
        var legacyAt = new DateTime(2020, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var legacyBack = new DateTime(2021, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var clearedAt = new DateTime(2022, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var clearedBack = new DateTime(2023, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var db = Db())
        {
            // A step-down copied in before the history existed names nobody; one whose Admins' accounts were
            // deleted under the retention rules (docs/adr/0014) has its references cleared.
            db.StaffStepDowns.Add(new StaffStepDown
            {
                UserId = 10, ChapterId = Chapter.ShrewsburyId, SteppedDownAt = legacyAt, RecordedBeforeHistory = true,
                ReinstatedAt = legacyBack, ReinstatedByUserId = 1
            });
            db.StaffStepDowns.Add(new StaffStepDown
            {
                UserId = 10, ChapterId = Chapter.ShrewsburyId, SteppedDownAt = clearedAt, SteppedDownByUserId = null,
                ReinstatedAt = clearedBack, ReinstatedByUserId = null
            });
            db.SaveChanges();
        }

        var cut = Render<MarkAsLeft>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Step down Tom Teacher']").Count == 1);

        Assert.Contains($"Stepped down {legacyAt:d MMMM yyyy}; reinstated {legacyBack:d MMMM yyyy} by Admin One", cut.Markup);
        Assert.Contains(
            $"Stepped down {clearedAt:d MMMM yyyy} by a former member of staff; reinstated {clearedBack:d MMMM yyyy} by a former member of staff",
            cut.Markup);
    }

    private VlmsDbContext Db() => new(
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);

    private bool SteppedDown()
    {
        using var db = Db();
        return db.AppUsers.Single(u => u.Id == 10).HasLeft;
    }
}
