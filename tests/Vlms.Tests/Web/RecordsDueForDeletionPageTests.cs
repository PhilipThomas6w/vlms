using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Retention;
using Vlms.Tests.Infrastructure;
using Vlms.Web.Components.Pages.Reporting;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// The records-due-for-deletion page's confirmation (docs/adr/0014-retention-purge.md decision 1):
/// a record is deleted only after the person's name is typed on the page, and "Delete all shown"
/// needs every name. The real page, services and SQLite database are used.
/// </summary>
public sealed class RecordsDueForDeletionPageTests : BunitContext, IDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public RecordsDueForDeletionPageTests()
    {
        _connectionString = $"Data Source=file:vlms-due-page-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using (var seed = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance))
        {
            seed.Database.EnsureCreated();
            seed.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            seed.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
            seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
            foreach (var (id, name) in new[] { (100, "Alex Old"), (101, "Blake Old") })
            {
                seed.Students.Add(new Student
                {
                    Id = id, Name = name, DateOfBirth = new DateOnly(2010, 1, 1), CurrentRankId = 1,
                    Status = StudentStatus.Inactive, EnrolmentDate = new DateOnly(2018, 1, 1), LeftAt = DateTime.UtcNow.AddYears(-4)
                });
            }

            seed.SaveChanges();
        }

        var auth = AddAuthorization();
        auth.SetAuthorized("Admin One");
        auth.SetRoles("Admin");

        var admin = new FakeCurrentUserContext(1, Role.Admin);
        Services.AddLogging();
        Services.AddSingleton<ICurrentUserContext>(admin);
        Services.AddSingleton<IBlobStorage>(new InMemoryBlobStorage());
        Services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        Services.AddScoped<RecordsDueForDeletionService>();
        Services.AddScoped<RetentionDeletionService>();
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
    public async Task Delete_IsEnabledOnlyOnceTheNameIsTyped_ThenDeletesThatRecordOnly()
    {
        var cut = Render<RecordsDueForDeletion>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Delete Alex Old']").Count == 1);

        await cut.Find("button[aria-label='Delete Alex Old']").ClickAsync(new());
        var confirm = cut.Find("button.btn--danger[aria-label='Delete Alex Old']");
        Assert.True(confirm.HasAttribute("disabled"));

        cut.Find("input#confirm-one-student-100").Input("Alex");
        Assert.True(cut.Find("button.btn--danger[aria-label='Delete Alex Old']").HasAttribute("disabled"));

        cut.Find("input#confirm-one-student-100").Input("Alex Old");
        Assert.False(cut.Find("button.btn--danger[aria-label='Delete Alex Old']").HasAttribute("disabled"));

        await cut.Find("button.btn--danger[aria-label='Delete Alex Old']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Alex Old deleted.", cut.Find("[role=status]").TextContent));
        Assert.Equal([101], StudentIds());
        Assert.Empty(cut.FindAll("button[aria-label='Delete Alex Old']"));
    }

    [Fact]
    public async Task Cancel_DeletesNothing()
    {
        var cut = Render<RecordsDueForDeletion>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Delete Alex Old']").Count == 1);

        await cut.Find("button[aria-label='Delete Alex Old']").ClickAsync(new());
        cut.Find("input#confirm-one-student-100").Input("Alex Old");
        await cut.Find("button[aria-label='Cancel deleting Alex Old']").ClickAsync(new());

        Assert.Equal([100, 101], StudentIds());
        Assert.Empty(cut.FindAll("input#confirm-one-student-100"));
    }

    [Fact]
    public async Task DeleteAllShown_NeedsEveryName()
    {
        var cut = Render<RecordsDueForDeletion>();
        cut.WaitForState(() => cut.FindAll("button[aria-label='Delete Alex Old']").Count == 1);

        await cut.FindAll("button").Single(b => b.TextContent == "Delete all shown").ClickAsync(new());
        cut.Find("input#confirm-all-student-100").Input("Alex Old");
        var deleteAll = cut.FindAll("button").Single(b => b.TextContent == "Delete all");
        Assert.True(deleteAll.HasAttribute("disabled"));

        cut.Find("input#confirm-all-student-101").Input("blake old");
        await cut.FindAll("button").Single(b => b.TextContent == "Delete all").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("2 records deleted.", cut.Find("[role=status]").TextContent));
        Assert.Empty(StudentIds());
    }

    private List<int> StudentIds()
    {
        using var db = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);
        return db.Students.Select(s => s.Id).OrderBy(id => id).ToList();
    }
}
