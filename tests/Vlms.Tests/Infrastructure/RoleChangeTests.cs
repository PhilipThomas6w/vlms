using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Retention;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// FR-016 (functional.md; quality/test-plan.md TC-026): a user is one identity with any combination
/// of roles, permissions are fixed per role, and a role change takes effect on the user's next action,
/// including in pages already open.
///
/// Each "session" below is one long-lived DI scope wired as Vlms.Web's Program.cs wires it: the
/// real <see cref="IdentityCurrentUserContext"/> built from an <see cref="AuthenticationStateProvider"/>,
/// the real authorization handlers and the same policies, and a <see cref="VlmsDbContext"/> whose
/// query filters consult that context. Keeping the scope open across a role change simulates an
/// interactive Blazor Server circuit, whose DI scope lasts as long as the circuit.
/// </summary>
public sealed class RoleChangeTests : IDisposable
{
    private const int AdminA = 1;          // Admin, MFA; makes the changes
    private const int AdminB = 2;          // Admin, MFA; so AdminA is never the last Admin
    private const int TeacherAdmin = 10;   // Teacher + Admin
    private const int TeacherSo = 11;      // Teacher + SafeguardingOfficer
    private const int TeacherParent = 12;  // Teacher + Parent, linked to Alex only
    private const int PlainTeacher = 13;   // Teacher only
    private const int Alex = 100;          // linked to TeacherParent
    private const int Sam = 200;           // not linked

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public RoleChangeTests()
    {
        _connectionString = $"Data Source=file:vlms-role-change-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();

        AddUser(db, AdminA, mfa: true, Role.Admin);
        AddUser(db, AdminB, mfa: true, Role.Admin);
        AddUser(db, TeacherAdmin, mfa: true, Role.Teacher, Role.Admin);
        AddUser(db, TeacherSo, mfa: true, Role.Teacher, Role.SafeguardingOfficer);
        AddUser(db, TeacherParent, mfa: true, Role.Teacher, Role.Parent);
        AddUser(db, PlainTeacher, mfa: true, Role.Teacher);

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Students.Add(NewStudent(Alex, "Alex"));
        db.Students.Add(NewStudent(Sam, "Sam"));
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Teacher Parent", ContactInfo = "tp@example.com", IsPrimary = true, AppUserId = TeacherParent });
        db.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Unlinked", ContactInfo = "u@example.com", IsPrimary = true });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = Alex, ParentGuardianId = 1, CreatedByUserId = AdminA });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = Sam, ParentGuardianId = 2, CreatedByUserId = AdminA });
        db.ConsentRecords.Add(NewConsentRecord(1, Alex, parentId: 1));
        db.ConsentRecords.Add(NewConsentRecord(2, Sam, parentId: 2));
        db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 1, ConsentRecordId = 1, EmergencyContact = "Alex contact" });
        db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = 2, ConsentRecordId = 2, EmergencyContact = "Sam contact" });
        db.DbsChecks.Add(new DbsCheck
        {
            Id = 1, TeacherUserId = PlainTeacher, CheckDate = new DateOnly(2025, 1, 1),
            ExpiryDate = new DateOnly(2028, 1, 1), CertificateNumber = "CERT-1", Status = DbsCheckStatus.Clear
        });
        db.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    // --- Multi-role users: policies and query filters admit the union of their roles. -------------

    [Fact]
    public async Task TeacherAndAdmin_PassesBothRolesPolicies_AndSeesTheUnionOfTheirData()
    {
        using var session = OpenSession(TeacherAdmin);

        Assert.True(await session.AuthorizeAsync("RequireTeacher"));
        Assert.True(await session.AuthorizeAsync("RequireAdmin"));
        Assert.True(await session.AuthorizeAsync("RequireAdminOrTeacher"));
        Assert.True(await session.AuthorizeAsync("RequireAdminOrSafeguardingOfficer"));
        Assert.False(await session.AuthorizeAsync("RequireSafeguardingOfficer"));
        Assert.False(await session.AuthorizeAsync("RequireParent"));

        // DbsCheck comes only from the Admin role; Teacher alone never sees it.
        Assert.Single(session.Db.DbsChecks.AsNoTracking().ToList());
        Assert.Equal(2, session.Db.ConsentSensitiveDetails.AsNoTracking().Count());
    }

    [Fact]
    public async Task TeacherAndSafeguardingOfficer_PassesBothRolesPolicies_AndSeesTheUnionOfTheirData()
    {
        using var session = OpenSession(TeacherSo);

        Assert.True(await session.AuthorizeAsync("RequireTeacher"));
        Assert.True(await session.AuthorizeAsync("RequireSafeguardingOfficer"));
        Assert.True(await session.AuthorizeAsync("RequireAdminOrSafeguardingOfficer"));
        Assert.True(await session.AuthorizeAsync("RequireAdminOrTeacher"));
        Assert.False(await session.AuthorizeAsync("RequireAdmin"));

        Assert.Single(session.Db.DbsChecks.AsNoTracking().ToList());
        Assert.Equal(2, session.Db.ConsentSensitiveDetails.AsNoTracking().Count());
    }

    [Fact]
    public async Task TeacherAndParent_PassesBothRolesPolicies_AndTheConsentFilterAdmitsBothArms()
    {
        using (var session = OpenSession(TeacherParent))
        {
            Assert.True(await session.AuthorizeAsync("RequireTeacher"));
            Assert.True(await session.AuthorizeAsync("RequireParent"));
            Assert.True(await session.AuthorizeAsync("RequireAdminOrTeacher"));
            Assert.False(await session.AuthorizeAsync("RequireAdminOrSafeguardingOfficer"));
            Assert.False(await session.AuthorizeAsync("RequireAdmin"));

            // Teacher arm (MFA on): every student's details. DbsCheck: neither role.
            Assert.Equal(2, session.Db.ConsentSensitiveDetails.AsNoTracking().Count());
            Assert.Empty(session.Db.DbsChecks.AsNoTracking().ToList());
        }

        // With MFA off the Teacher arm closes, and the Parent arm still admits the linked child only.
        await SetMfaAsync(TeacherParent, enabled: false);
        using (var session = OpenSession(TeacherParent))
        {
            var visible = session.Db.ConsentSensitiveDetails.AsNoTracking().Select(d => d.EmergencyContact).ToList();
            Assert.Equal(["Alex contact"], visible);
        }
    }

    [Fact]
    public void TeacherAndParent_LosingTheParentRole_KeepsTheTeacherArm_AndLosingTeacherKeepsTheParentArm()
    {
        using var session = OpenSession(TeacherParent);
        Assert.Equal(2, session.Db.ConsentSensitiveDetails.AsNoTracking().Count());

        RemoveRoleDirectly(TeacherParent, Role.Teacher);
        Assert.Equal(["Alex contact"],
            session.Db.ConsentSensitiveDetails.AsNoTracking().Select(d => d.EmergencyContact).ToList());

        RemoveRoleDirectly(TeacherParent, Role.Parent);
        Assert.Empty(session.Db.ConsentSensitiveDetails.AsNoTracking().ToList());
    }

    // --- Changes take effect within the same (long-lived) scope. -----------------------------------

    [Fact]
    public async Task RoleRemoved_AfterTheContextWasFirstUsed_IsNotHonouredOnTheNextCheckInTheSameScope()
    {
        using var session = OpenSession(TeacherAdmin);

        // First use: the session holds Admin.
        Assert.True(session.CurrentUser.HasRole(Role.Admin));
        Assert.True(await session.AuthorizeAsync("RequireAdmin"));
        Assert.Single(session.Db.DbsChecks.AsNoTracking().ToList());

        // An Admin elsewhere removes the role.
        await AsAdminAsync(service => service.RevokeAsync(TeacherAdmin, Role.Admin));

        // Same scope, next check: no longer honoured, and the Teacher role is untouched.
        Assert.False(session.CurrentUser.HasRole(Role.Admin));
        Assert.False(await session.AuthorizeAsync("RequireAdmin"));
        Assert.Empty(session.Db.DbsChecks.AsNoTracking().ToList());
        Assert.True(await session.AuthorizeAsync("RequireTeacher"));
    }

    [Fact]
    public async Task RoleGranted_TakesEffectOnTheNextCheckInTheSameScope()
    {
        using var session = OpenSession(PlainTeacher);

        Assert.False(await session.AuthorizeAsync("RequireSafeguardingOfficer"));
        Assert.Empty(session.Db.DbsChecks.AsNoTracking().ToList());

        await AsAdminAsync(service => service.GrantAsync(PlainTeacher, Role.SafeguardingOfficer));

        Assert.True(session.CurrentUser.HasRole(Role.SafeguardingOfficer));
        Assert.True(await session.AuthorizeAsync("RequireSafeguardingOfficer"));
        Assert.Single(session.Db.DbsChecks.AsNoTracking().ToList());
    }

    [Fact]
    public async Task MfaDisabled_TakesEffectOnTheNextCheckInTheSameScope()
    {
        using var session = OpenSession(TeacherAdmin);
        Assert.True(await session.AuthorizeAsync("RequireAdmin"));
        Assert.Single(session.Db.DbsChecks.AsNoTracking().ToList());

        await SetMfaAsync(TeacherAdmin, enabled: false);

        Assert.False(session.CurrentUser.HasMfaEnabled);
        Assert.False(await session.AuthorizeAsync("RequireAdmin"));
        Assert.Empty(session.Db.DbsChecks.AsNoTracking().ToList());
        Assert.Empty(session.Db.ConsentSensitiveDetails.AsNoTracking().ToList());
    }

    // --- Security stamp. ---------------------------------------------------------------------------

    [Fact]
    public async Task GrantingARole_UpdatesTheSecurityStamp()
    {
        var before = await StampOf(PlainTeacher);

        await AsAdminAsync(service => service.GrantAsync(PlainTeacher, Role.Approver));

        Assert.NotEqual(before, await StampOf(PlainTeacher));
    }

    [Fact]
    public async Task RevokingARole_UpdatesTheSecurityStamp()
    {
        var before = await StampOf(TeacherAdmin);

        await AsAdminAsync(service => service.RevokeAsync(TeacherAdmin, Role.Admin));

        Assert.NotEqual(before, await StampOf(TeacherAdmin));
    }

    [Fact]
    public async Task ApprovingAGuardianAsParent_UpdatesTheSecurityStamp()
    {
        using (var db = NewContext())
        {
            AddUser(db, 20, mfa: false);
            db.GuardianInvites.Add(new GuardianInvite
            {
                Id = 1, StudentId = Sam, ParentGuardianId = 2, CreatedByUserId = AdminA,
                CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
                ConsumedAt = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
                ConsumedByAppUserId = 20
            });
            db.SaveChanges();
        }
        var before = await StampOf(20);

        await AsAdminAsync(service => service.ApproveGuardianAsParentAsync(20));

        Assert.NotEqual(before, await StampOf(20));
    }

    [Fact]
    public async Task MarkingATeacherAsLeft_UpdatesTheSecurityStamp()
    {
        var before = await StampOf(PlainTeacher);

        using (var scope = BuildServices(new FakeCurrentUserContext(AdminA, Role.Admin)).CreateScope())
        {
            var service = new LeavingService(
                scope.ServiceProvider.GetRequiredService<VlmsDbContext>(),
                scope.ServiceProvider.GetRequiredService<ICurrentUserContext>(),
                scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());
            await service.MarkTeacherLeftAsync(PlainTeacher);
        }

        Assert.NotEqual(before, await StampOf(PlainTeacher));
    }

    [Fact]
    public async Task DisablingTwoFactor_UpdatesTheSecurityStamp_ThroughUserManager()
    {
        // TwoFactorAuthentication.razor disables 2FA through UserManager.SetTwoFactorEnabledAsync;
        // this pins that it rotates the stamp itself, so that page needs no extra call.
        var before = await StampOf(TeacherAdmin);

        await SetMfaAsync(TeacherAdmin, enabled: false);

        Assert.NotEqual(before, await StampOf(TeacherAdmin));
    }

    [Fact]
    public async Task RefusedGrant_ToAStudentSignIn_ChangesNeitherRolesNorStamp()
    {
        // The Student-only rule (FR-011, adr/0007) is unchanged by FR-016.
        using (var db = NewContext())
        {
            AddUser(db, 50, mfa: false, Role.Student);
            db.Students.Add(new Student
            {
                Id = 300, Name = "Signed-in Student", DateOfBirth = new DateOnly(2014, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 1, 1), AppUserId = 50
            });
            db.SaveChanges();
        }
        var before = await StampOf(50);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AsAdminAsync(s => s.GrantAsync(50, Role.Teacher)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AsAdminAsync(s => s.GrantAsync(PlainTeacher, Role.Student)));

        Assert.Equal(before, await StampOf(50));
        using var verify = NewContext();
        Assert.Equal([Role.Student], verify.UserRoles.Where(r => r.UserId == 50).Select(r => r.Role).ToList());
        Assert.Equal([Role.Teacher], verify.UserRoles.Where(r => r.UserId == PlainTeacher).Select(r => r.Role).ToList());
    }

    // --- Helpers. ----------------------------------------------------------------------------------

    private sealed class Session(ServiceProvider provider, IServiceScope scope, ClaimsPrincipal principal) : IDisposable
    {
        public ICurrentUserContext CurrentUser { get; } = scope.ServiceProvider.GetRequiredService<ICurrentUserContext>();
        public VlmsDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        public async Task<bool> AuthorizeAsync(string policy) =>
            (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, policy)).Succeeded;

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    /// <summary>One long-lived scope for <paramref name="userId"/>, wired as Program.cs wires it.</summary>
    private Session OpenSession(int userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture))], "Identity.Application"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        services.AddScoped<AuthenticationStateProvider>(_ => new FixedAuthenticationStateProvider(principal));
        services.AddScoped<ICurrentUserContext>(sp => new IdentityCurrentUserContext(
            sp.GetRequiredService<AuthenticationStateProvider>(),
            sp.GetRequiredService<DbContextOptions<VlmsDbContext>>()));
        services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        services.AddAuthorizationCore(options =>
        {
            // The same role policies as Program.cs.
            foreach (var role in Enum.GetValues<Role>())
            {
                options.AddPolicy($"Require{role}", policy =>
                {
                    policy.Requirements.Add(new RoleRequirement(role));
                    policy.Requirements.Add(new MfaRequirement());
                });
            }
            options.AddPolicy("RequireAdminOrTeacher", policy =>
            {
                policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher));
                policy.Requirements.Add(new MfaRequirement());
            });
            options.AddPolicy("RequireAdminOrSafeguardingOfficer", policy =>
            {
                policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.SafeguardingOfficer));
                policy.Requirements.Add(new MfaRequirement());
            });
        });

        var provider = services.BuildServiceProvider();
        return new Session(provider, provider.CreateScope(), principal);
    }

    private ServiceProvider BuildServices(ICurrentUserContext currentUser)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        services.AddScoped<RoleAssignmentService>();
        return services.BuildServiceProvider();
    }

    private async Task AsAdminAsync(Func<RoleAssignmentService, Task> action)
    {
        using var provider = BuildServices(new FakeCurrentUserContext(AdminA, Role.Admin));
        using var scope = provider.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RoleAssignmentService>());
    }

    private async Task SetMfaAsync(int userId, bool enabled)
    {
        using var provider = BuildServices(new FakeCurrentUserContext(null));
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await users.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await users.SetTwoFactorEnabledAsync(user!, enabled);
        Assert.True(result.Succeeded);
    }

    private void RemoveRoleDirectly(int userId, Role role)
    {
        using var db = NewContext();
        db.UserRoles.Remove(db.UserRoles.Single(r => r.UserId == userId && r.Role == role));
        db.SaveChanges();
    }

    private async Task<string?> StampOf(int userId)
    {
        using var db = NewContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
    }

    private VlmsDbContext NewContext() => new(
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
        NullCurrentUserContext.Instance);

    private static void AddUser(VlmsDbContext db, int id, bool mfa, params Role[] roles)
    {
        var email = $"user{id}@example.com";
        db.Users.Add(new VlmsIdentityUser
        {
            Id = id, UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            TwoFactorEnabled = mfa, SecurityStamp = Guid.NewGuid().ToString("N")
        });
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = email });
        foreach (var role in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role });
        }
    }

    private static Student NewStudent(int id, string name) => new()
    {
        Id = id, Name = name, DateOfBirth = new DateOnly(2015, 1, 1), CurrentRankId = 1,
        Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
    };

    private static ConsentRecord NewConsentRecord(int id, int studentId, int parentId) => new()
    {
        Id = id, StudentId = studentId, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31),
        PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
        Status = ConsentStatus.Approved, SubmittedByParentId = parentId, ApprovedByUserId = AdminA,
        ExpiryDate = new DateOnly(2026, 12, 31)
    };
}
