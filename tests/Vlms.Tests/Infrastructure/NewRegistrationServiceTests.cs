using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="NewRegistrationService"/> (functional.md FR-021 as amended 09/11/2026): a
/// registration names its chapter, appears on that chapter's list only, emails that chapter's
/// Admins, grants no role, leaves the list once a role is granted or it is dismissed; and a
/// National Administrator places role-less accounts that are on no list.
/// </summary>
public sealed class NewRegistrationServiceTests : IDisposable
{
    private const int Coventry = 2;
    private const int ClosedChapter = 3;
    private const string Password = "Correct-Horse-Battery-9";

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public NewRegistrationServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-new-registration-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var caller = Caller(new FakeCurrentUserContext(null));
        var db = caller.Db;
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        db.Chapters.Add(new Chapter { Id = ClosedChapter, Name = "Closed", Place = "Nowhere", CreatedAt = DateTime.UtcNow, IsActive = false });
        db.AppUsers.Add(new AppUser { Id = 9001, DisplayName = "Shrewsbury Admin", Email = "admin.shr@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 9001, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
        db.AppUsers.Add(new AppUser { Id = 9002, DisplayName = "Coventry Admin", Email = "admin.cov@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 9002, Role = Role.Admin, ChapterId = Coventry });
        db.AppUsers.Add(new AppUser { Id = 9003, DisplayName = "National", Email = "national@example.com" });
        db.UserRoles.Add(new UserRole { UserId = 9003, Role = Role.NationalAdministrator });
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private sealed class CallerScope(ServiceProvider provider, IServiceScope scope) : IDisposable
    {
        public VlmsDbContext Db => scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        public NewRegistrationService Service => scope.ServiceProvider.GetRequiredService<NewRegistrationService>();
        public RoleAssignmentService Roles => scope.ServiceProvider.GetRequiredService<RoleAssignmentService>();
        public FakeNotificationService Emails => provider.GetRequiredService<FakeNotificationService>();

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private CallerScope Caller(ICurrentUserContext user)
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, user, configure: services =>
        {
            services.AddScoped<RoleAssignmentService>();
            services.AddScoped(sp => new NewRegistrationService(
                sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(),
                sp.GetRequiredService<AppUserProvisioningService>(), sp.GetRequiredService<INotificationService>(),
                NullLogger<NewRegistrationService>.Instance, "https://test.example"));
        });
        return new CallerScope(provider, provider.CreateScope());
    }

    private CallerScope Anonymous() => Caller(new FakeCurrentUserContext(null));
    private CallerScope ShrewsburyAdmin() => Caller(new FakeCurrentUserContext(9001, Role.Admin) { ActiveChapterId = Chapter.ShrewsburyId });
    private CallerScope CoventryAdmin() => Caller(new FakeCurrentUserContext(9002, Role.Admin) { ActiveChapterId = Coventry });
    private CallerScope National(bool mfa = true) => Caller(new FakeCurrentUserContext(9003, mfa, Role.NationalAdministrator) { ActiveChapterId = null });

    private async Task<int> RegisterAsync(string email, int chapterId, JoiningAs joiningAs = JoiningAs.Teacher)
    {
        using var anon = Anonymous();
        var (result, user) = await anon.Service.RegisterAsync(email, Password, "Name " + email, chapterId, joiningAs);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        return user!.Id;
    }

    [Fact]
    public async Task Register_CreatesAccountWithNoRole_OnChosenChapterListOnly()
    {
        var id = await RegisterAsync("teacher@example.com", Chapter.ShrewsburyId);

        using (var check = Anonymous())
        {
            Assert.False(await check.Db.UserRoles.AnyAsync(r => r.UserId == id));
        }

        using (var shr = ShrewsburyAdmin())
        {
            var listed = Assert.Single(await shr.Service.ListAsync());
            Assert.Equal(id, listed.UserId);
            Assert.Equal(JoiningAs.Teacher, listed.JoiningAs);
            Assert.False(listed.EmailConfirmed);
        }

        using var cov = CoventryAdmin();
        Assert.Empty(await cov.Service.ListAsync());
    }

    [Fact]
    public async Task Register_EmailsOnlyTheChosenChaptersAdmins_WithNameAndJoiningAs()
    {
        using var anon = Anonymous();
        await anon.Service.RegisterAsync("parent@example.com", Password, "Mary Parent", Coventry, JoiningAs.Parent);

        var sent = Assert.Single(anon.Emails.SentRequests);
        Assert.Equal("admin.cov@example.com", sent.RecipientEmail);
        Assert.Contains("Mary Parent", sent.Subject);
        Assert.Contains("parent", sent.Body);
        Assert.Contains("https://test.example/administration/user-roles", sent.Body);
    }

    [Fact]
    public async Task Register_ClosedOrUnknownChapter_Fails_AndWritesNothing()
    {
        using var anon = Anonymous();
        foreach (var chapterId in new[] { ClosedChapter, 999 })
        {
            var (result, user) = await anon.Service.RegisterAsync($"x{chapterId}@example.com", Password, "X", chapterId, JoiningAs.Other);
            Assert.False(result.Succeeded);
            Assert.Null(user);
        }

        Assert.False(await anon.Db.AppUsers.AnyAsync(u => u.Email.StartsWith("x")));
        Assert.Empty(anon.Emails.SentRequests);
    }

    [Fact]
    public async Task Register_IdentityFailure_WritesNoRequest()
    {
        using var anon = Anonymous();
        var (result, _) = await anon.Service.RegisterAsync("weak@example.com", "short", "Weak", Chapter.ShrewsburyId, JoiningAs.Teacher);

        Assert.False(result.Succeeded);
        Assert.False(await anon.Db.ChapterJoinRequests.AnyAsync());
        Assert.Empty(anon.Emails.SentRequests);
    }

    [Fact]
    public async Task GrantingARole_TakesThemOffTheList()
    {
        var id = await RegisterAsync("teacher@example.com", Chapter.ShrewsburyId);

        using (var shr = ShrewsburyAdmin())
        {
            await shr.Roles.GrantAsync(id, Role.Teacher);
        }

        using var verify = ShrewsburyAdmin();
        Assert.Empty(await verify.Service.ListAsync());
    }

    [Fact]
    public async Task Dismiss_TakesThemOffTheList_AndDeletesNothing()
    {
        var id = await RegisterAsync("someone@example.com", Chapter.ShrewsburyId, JoiningAs.Other);

        using (var shr = ShrewsburyAdmin())
        {
            await shr.Service.DismissAsync(id);
        }

        using var verify = ShrewsburyAdmin();
        Assert.Empty(await verify.Service.ListAsync());
        Assert.True(await verify.Db.AppUsers.AnyAsync(u => u.Id == id));
        Assert.True(await verify.Db.ChapterJoinRequests.AnyAsync(r => r.UserId == id && r.DismissedByUserId == 9001));
    }

    [Fact]
    public async Task Dismiss_SomeoneOnAnotherChaptersList_Throws()
    {
        var id = await RegisterAsync("cov@example.com", Coventry);

        using var shr = ShrewsburyAdmin();
        await Assert.ThrowsAsync<InvalidOperationException>(() => shr.Service.DismissAsync(id));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Parent)]
    [InlineData(Role.NationalAdministrator)]
    public async Task AdminMethods_ByNonAdmin_Throw(Role role)
    {
        using var caller = Caller(new FakeCurrentUserContext(9003, role));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.DismissAsync(1));
    }

    [Fact]
    public async Task NationalMethods_ByChapterAdminOrWithoutMfa_Throw()
    {
        using (var shr = ShrewsburyAdmin())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => shr.Service.ListUnplacedAsync());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => shr.Service.PlaceAsync(1, Chapter.ShrewsburyId));
        }

        using var noMfa = National(mfa: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noMfa.Service.ListUnplacedAsync());
    }

    [Fact]
    public async Task Unplaced_ListsRolelessAccountsOnNoList_AndPlaceSendsToChapterAndEmailsItsAdmins()
    {
        using (var seed = Anonymous())
        {
            // Registered before the Register page asked for a chapter.
            seed.Db.AppUsers.Add(new AppUser { Id = 500, DisplayName = "Early Teacher", Email = "early@example.com" });
            // A student sign-in has no email and is never listed.
            seed.Db.AppUsers.Add(new AppUser { Id = 501, DisplayName = "A Student", Email = "" });
            seed.Db.SaveChanges();
        }

        var listedId = await RegisterAsync("listed@example.com", Chapter.ShrewsburyId);

        using (var national = National())
        {
            var unplaced = await national.Service.ListUnplacedAsync();
            Assert.Equal([500], unplaced.Select(u => u.UserId));

            await national.Service.PlaceAsync(500, Coventry);
            var sent = Assert.Single(national.Emails.SentRequests);
            Assert.Equal("admin.cov@example.com", sent.RecipientEmail);

            Assert.Empty(await national.Service.ListUnplacedAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => national.Service.PlaceAsync(500, Chapter.ShrewsburyId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => national.Service.PlaceAsync(listedId, Coventry));
        }

        using var cov = CoventryAdmin();
        var placed = Assert.Single(await cov.Service.ListAsync());
        Assert.Equal(500, placed.UserId);
        Assert.Null(placed.JoiningAs);
        Assert.True(placed.PlacedByNationalAdministrator);
    }

    [Fact]
    public async Task DismissedEverywhere_ReturnsToTheNationalList()
    {
        var id = await RegisterAsync("wrong@example.com", Chapter.ShrewsburyId);
        using (var shr = ShrewsburyAdmin())
        {
            await shr.Service.DismissAsync(id);
        }

        using var national = National();
        Assert.Contains(id, (await national.Service.ListUnplacedAsync()).Select(u => u.UserId));
    }

    [Fact]
    public async Task Place_ClosedChapter_Throws()
    {
        using (var seed = Anonymous())
        {
            seed.Db.AppUsers.Add(new AppUser { Id = 600, DisplayName = "Someone", Email = "someone@example.com" });
            seed.Db.SaveChanges();
        }

        using var national = National();
        await Assert.ThrowsAsync<InvalidOperationException>(() => national.Service.PlaceAsync(600, ClosedChapter));
    }
}
