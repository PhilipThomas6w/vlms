using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Authorization;

/// <summary>
/// The account-wide MFA rule (docs/adr/0010-chapters.md assumption 9, amended by Philip 27/10/2026;
/// test-plan.md TC-047a): an account holding Teacher, Admin or Safeguarding Officer in any chapter
/// needs MFA whatever its active chapter, including one where it is only a Parent. A staff leaver's
/// staff rows do not count (FR-017). Real <see cref="IdentityCurrentUserContext"/> over SQLite in
/// memory, with the real <see cref="MfaRequirementHandler"/> and <see cref="ChapterService.SwitchToAsync"/>.
/// </summary>
public sealed class CrossChapterMfaTests : IDisposable
{
    private const int Coventry = 2;

    private const int StaffInAParentInB = 1;   // Teacher in Shrewsbury, Parent in Coventry, no MFA
    private const int ParentOnly = 2;          // Parent in Coventry, no MFA
    private const int LeaverParent = 3;        // Teacher in Shrewsbury (left), Parent in Coventry, no MFA

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public CrossChapterMfaTests()
    {
        _connectionString = $"Data Source=file:vlms-cross-chapter-mfa-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = new VlmsDbContext(Options(), TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = DateTime.UtcNow, IsActive = true });
        AddUser(db, StaffInAParentInB, hasLeft: false, (Role.Teacher, Chapter.ShrewsburyId), (Role.Parent, Coventry));
        AddUser(db, ParentOnly, hasLeft: false, (Role.Parent, Coventry));
        AddUser(db, LeaverParent, hasLeft: true, (Role.Teacher, Chapter.ShrewsburyId), (Role.Parent, Coventry));
        db.SaveChanges();
    }

    public void Dispose() => _anchor.Dispose();

    private DbContextOptions<VlmsDbContext> Options() =>
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options;

    private static void AddUser(VlmsDbContext db, int id, bool hasLeft, params (Role Role, int Chapter)[] roles)
    {
        var email = $"user{id}@example.com";
        db.Users.Add(new VlmsIdentityUser
        {
            Id = id, UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
            NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, TwoFactorEnabled = false,
            SecurityStamp = Guid.NewGuid().ToString("N")
        });
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = email, HasLeft = hasLeft });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    private IdentityCurrentUserContext ContextFor(int userId) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))], "test")),
        Options());

    private void ChooseChapter(int userId, int chapterId)
    {
        using var db = new VlmsDbContext(Options(), TestSeedUserContext.Instance);
        db.ActiveChapterChoices.Add(new ActiveChapterChoice { UserId = userId, ChapterId = chapterId });
        db.SaveChanges();
    }

    private void EnableMfa(int userId)
    {
        using var db = new VlmsDbContext(Options(), TestSeedUserContext.Instance);
        db.Users.Single(u => u.Id == userId).TwoFactorEnabled = true;
        db.SaveChanges();
    }

    private static async Task<bool> MfaRequirementMet(ICurrentUserContext user)
    {
        var context = new AuthorizationHandlerContext([new MfaRequirement()], new ClaimsPrincipal(new ClaimsIdentity()), resource: null);
        await new MfaRequirementHandler(user).HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task StaffInOneChapter_WorkingInAnotherWhereOnlyAParent_NeedsMfa()
    {
        ChooseChapter(StaffInAParentInB, Coventry);
        var current = ContextFor(StaffInAParentInB);

        Assert.Equal(Coventry, current.ActiveChapterId);
        Assert.True(current.HasRole(Role.Parent));
        Assert.False(current.HasRole(Role.Teacher));
        Assert.True(current.HoldsStaffRoleInAnyChapter);
        Assert.False(await MfaRequirementMet(current));

        EnableMfa(StaffInAParentInB);
        Assert.True(await MfaRequirementMet(current));
    }

    [Fact]
    public async Task AParentOnly_DoesNotNeedMfa()
    {
        var current = ContextFor(ParentOnly);

        Assert.False(current.HoldsStaffRoleInAnyChapter);
        Assert.True(await MfaRequirementMet(current));
    }

    [Fact]
    public async Task AStaffLeaverWhoIsAParent_KeepsParentAccessWithoutMfa()
    {
        var current = ContextFor(LeaverParent);

        Assert.Equal(Coventry, current.ActiveChapterId);
        Assert.True(current.HasRole(Role.Parent));
        Assert.False(current.HoldsStaffRoleInAnyChapter);
        Assert.True(await MfaRequirementMet(current));
    }

    [Fact]
    public async Task SwitchingChapter_DoesNotLiftTheMfaRequirement()
    {
        var current = ContextFor(StaffInAParentInB);
        Assert.Equal(Chapter.ShrewsburyId, current.ActiveChapterId);
        Assert.False(await MfaRequirementMet(current));

        using var provider = TestIdentityServices.BuildServiceProvider(_connectionString, current);
        using var scope = provider.CreateScope();
        var chapters = new ChapterService(scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), current,
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());

        await chapters.SwitchToAsync(Coventry);
        Assert.Equal(Coventry, current.ActiveChapterId);
        Assert.False(await MfaRequirementMet(current));

        await chapters.SwitchToAsync(Chapter.ShrewsburyId);
        Assert.Equal(Chapter.ShrewsburyId, current.ActiveChapterId);
        Assert.False(await MfaRequirementMet(current));
    }

    [Fact]
    public async Task TheTestDouble_AppliesTheSameRule()
    {
        var parentHere = new FakeCurrentUserContext(1, hasMfaEnabled: false, Role.Parent) { StaffRoleInAnotherChapter = true };

        Assert.False(await MfaRequirementMet(parentHere));
    }
}
