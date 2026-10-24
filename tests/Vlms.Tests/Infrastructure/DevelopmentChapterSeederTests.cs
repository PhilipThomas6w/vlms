using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The Development seed for the Coventry test chapter (FR-021, docs/adr/0010-chapters.md; TC-051):
/// its own Admin, Teacher, Safeguarding Officer, parent and student, all in Coventry, and a second
/// run changes nothing.
/// </summary>
public sealed class DevelopmentChapterSeederTests : IDisposable
{
    private const string Password = "Dev!Passw0rd";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public DevelopmentChapterSeederTests()
    {
        _connectionString = $"Data Source=file:vlms-devseed-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = new VlmsDbContext(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private async Task<DevelopmentSeedResult> RunAsync()
    {
        using var provider = TestIdentityServices.BuildServiceProvider(_connectionString, SystemCurrentUserContext.Instance);
        using var scope = provider.CreateScope();
        var seeder = new DevelopmentChapterSeeder(
            scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>());
        return await seeder.SeedAsync(Password);
    }

    [Fact]
    public async Task Seed_CreatesCoventry_WithItsOwnStaffParentAndStudent_AndIsIdempotent()
    {
        var first = await RunAsync();
        var second = await RunAsync();

        Assert.True(first.ChapterCreated);
        Assert.False(second.ChapterCreated);
        Assert.Empty(second.AccountsCreated);

        using var db = new VlmsDbContext(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, TestSeedUserContext.Instance);
        var chapter = db.Chapters.Single(c => c.Name == DevelopmentChapterSeeder.CoventryName);
        Assert.NotEqual(Chapter.ShrewsburyId, chapter.Id);

        var roles = db.UserRoles.Where(r => r.ChapterId == chapter.Id).Select(r => r.Role).ToList();
        Assert.Equal(
            new[] { Role.Admin, Role.Teacher, Role.Parent, Role.Student, Role.SafeguardingOfficer }.Order(),
            roles.Order());
        Assert.DoesNotContain(db.UserRoles.ToList(), r => r.ChapterId == Chapter.ShrewsburyId);

        var student = db.Students.Single();
        Assert.Equal(chapter.Id, student.ChapterId);
        Assert.NotNull(student.AppUserId);
        Assert.Equal(chapter.Id, db.StudentGuardianLinks.Single().ChapterId);
        Assert.Equal(chapter.Id, db.ConsentRecords.Single(c => c.Status == ConsentStatus.Approved).ChapterId);
    }
}
