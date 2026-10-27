using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Tests.Infrastructure;
using Vlms.Web.Components.Pages.Guardianship;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// The guardian links page names who created each link; a link created by an account since deleted under the
/// retention rules (docs/adr/0014, settled after build) keeps its row and reads "a former member of staff". The real
/// page, services and SQLite database are used.
/// </summary>
public sealed class GuardianLinksPageTests : BunitContext, IDisposable
{
    private const int AdminId = 1, FormerAdminId = 2;

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public GuardianLinksPageTests()
    {
        _connectionString = $"Data Source=file:vlms-guardian-links-page-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using (var seed = Db())
        {
            seed.Database.EnsureCreated();
            seed.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            seed.AppUsers.Add(new AppUser { Id = AdminId, DisplayName = "Admin One", Email = "admin@example.com" });
            seed.AppUsers.Add(new AppUser { Id = FormerAdminId, DisplayName = "Admin Two", Email = "admin2@example.com" });
            seed.UserRoles.Add(new UserRole { UserId = AdminId, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
            foreach (var (id, name) in new[] { (100, "Alex Able"), (101, "Bea Bold") })
            {
                seed.Students.Add(new Student
                {
                    Id = id, Name = name, DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1, Status = StudentStatus.Active,
                    EnrolmentDate = new DateOnly(2024, 9, 1)
                });
            }
            seed.ParentGuardians.Add(new ParentGuardian { Id = 200, Name = "Pat Parent", ContactInfo = "pat@example.com", IsPrimary = true });
            seed.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 200, CreatedByUserId = AdminId });
            seed.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 101, ParentGuardianId = 200, CreatedByUserId = FormerAdminId });
            seed.SaveChanges();

            // The second Admin's account is deleted: the database clears the link's reference to it.
            seed.AppUsers.Remove(seed.AppUsers.Single(u => u.Id == FormerAdminId));
            seed.SaveChanges();
        }

        var auth = AddAuthorization();
        auth.SetAuthorized("Admin One");
        auth.SetRoles("Admin");

        TestIdentityServices.AddTo(Services, _connectionString, new FakeCurrentUserContext(AdminId, Role.Admin));
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
    public void ALinkCreatedByADeletedAccount_IsKept_AndReadsAFormerMemberOfStaff()
    {
        using (var db = Db())
        {
            Assert.Null(db.StudentGuardianLinks.IgnoreQueryFilters().Single(l => l.StudentId == 101).CreatedByUserId);
        }

        var cut = Render<GuardianLinks>();
        cut.WaitForState(() => cut.FindAll("td[data-label='Created by']").Count == 2);

        var createdBy = cut.FindAll("tr")
            .Where(row => row.QuerySelector("td[data-label='Student']") is not null)
            .ToDictionary(
                row => row.QuerySelector("td[data-label='Student']")!.TextContent,
                row => row.QuerySelector("td[data-label='Created by']")!.TextContent);
        Assert.Equal("Admin One", createdBy["Alex Able"]);
        Assert.Equal("a former member of staff", createdBy["Bea Bold"]);
    }

    private VlmsDbContext Db() => new(
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);
}
