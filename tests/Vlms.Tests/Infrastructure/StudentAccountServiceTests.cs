using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Vlms.Web.Components.Account;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies student sign-ins (functional.md FR-011, quality/test-plan.md TC-021,
/// adr/0007-student-sign-in-accounts.md): only a linked parent who hasn't left creates one; one per
/// student; usernames are unique and never email-shaped; reset by the linked parent or an Admin
/// only; the student signs in with the username and gets the Student role; student sign-ins have no
/// email, and the adult confirmed-email and forgot-password rules are unchanged.
///
/// Real Identity wiring via <see cref="TestIdentityServices"/>, over shared-cache in-memory SQLite.
/// </summary>
public sealed class StudentAccountServiceTests : IDisposable
{
    private const string Password = "Str0ng!Passw0rd";
    private const string NewPassword = "N3w!Passw0rdX";

    private const int AdminUser = 9009;
    private const int TeacherUser = 9008;
    private const int ParentA = 9001;      // linked to Alex (100)
    private const int ParentB = 9002;      // linked to Sam (200)
    private const int LeftParent = 9003;   // linked to Alex (100), has left
    private const int ParentC = 9004;      // linked to Ivy, Jo and Kim (301-303)
    private const int Alex = 100;
    private const int Sam = 200;
    private const int Ivy = 301;
    private const int Jo = 302;
    private const int Kim = 303;

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public StudentAccountServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-studentaccount-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();

        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.AppUsers.Add(new AppUser { Id = AdminUser, DisplayName = "Admin", Email = "admin@example.com" });
        db.AppUsers.Add(new AppUser { Id = TeacherUser, DisplayName = "Teacher", Email = "teacher@example.com" });
        db.AppUsers.Add(new AppUser { Id = ParentA, DisplayName = "Parent A", Email = "a@example.com" });
        db.AppUsers.Add(new AppUser { Id = ParentB, DisplayName = "Parent B", Email = "b@example.com" });
        db.AppUsers.Add(new AppUser
        {
            Id = LeftParent, DisplayName = "Left Parent", Email = "left@example.com",
            HasLeft = true, LeftAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Parent A", ContactInfo = "a@example.com", IsPrimary = true, AppUserId = ParentA });
        db.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Parent B", ContactInfo = "b@example.com", IsPrimary = true, AppUserId = ParentB });
        db.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Left Parent", ContactInfo = "left@example.com", IsPrimary = false, AppUserId = LeftParent });
        db.Students.Add(NewStudent(Alex, "Alex"));
        db.Students.Add(NewStudent(Sam, "Sam"));
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = Alex, ParentGuardianId = 1, CreatedByUserId = TeacherUser });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = Sam, ParentGuardianId = 2, CreatedByUserId = TeacherUser });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = Alex, ParentGuardianId = 3, CreatedByUserId = TeacherUser });
        db.AppUsers.Add(new AppUser { Id = ParentC, DisplayName = "Parent C", Email = "c@example.com" });
        db.ParentGuardians.Add(new ParentGuardian { Id = 4, Name = "Parent C", ContactInfo = "c@example.com", IsPrimary = true, AppUserId = ParentC });
        foreach (var (id, name) in new[] { (Ivy, "Ivy"), (Jo, "Jo"), (Kim, "Kim") })
        {
            db.Students.Add(NewStudent(id, name));
            db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = id, ParentGuardianId = 4, CreatedByUserId = TeacherUser });
        }

        db.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private static Student NewStudent(int id, string name) => new()
    {
        Id = id, Name = name, DateOfBirth = new DateOnly(2016, 1, 1), CurrentRankId = 1,
        Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 1, 1)
    };

    private VlmsDbContext NewContext() => new(
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
        NullCurrentUserContext.Instance);

    private sealed class Caller(ServiceProvider provider, IServiceScope scope) : IDisposable
    {
        public StudentAccountService Service { get; } = scope.ServiceProvider.GetRequiredService<StudentAccountService>();
        public IServiceProvider Services => scope.ServiceProvider;

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Caller As(int? userId, params Role[] roles) => As(new FakeCurrentUserContext(userId, roles));

    private Caller Anonymous() => As(new FakeCurrentUserContext(null));

    private Caller As(ICurrentUserContext user)
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, user, configure: services =>
        {
            // The real sign-in policy (RequireConfirmedAccount, lockout), as Program.cs applies it.
            services.Configure<IdentityOptions>(VlmsIdentityPolicy.Apply);
            services.AddAuthentication();
            services.AddHttpContextAccessor();
            new IdentityBuilder(typeof(VlmsIdentityUser), services)
                .AddSignInManager()
                .AddUserConfirmation<VlmsUserConfirmation>();
        });
        return new Caller(provider, provider.CreateScope());
    }

    private async Task CreateForAlexAsync(string username = "alex")
    {
        using var parent = As(ParentA, Role.Parent);
        var result = await parent.Service.CreateSignInAsync(Alex, username, Password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
    }

    private async Task<bool> PasswordWorksAsync(string username, string password)
    {
        using var caller = Anonymous();
        var users = caller.Services.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await users.FindByNameAsync(username);
        return user is not null && await users.CheckPasswordAsync(user, password);
    }

    private async Task<SignInResult> SignInAsync(string username, string password)
    {
        using var caller = Anonymous();
        var users = caller.Services.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var signIn = caller.Services.GetRequiredService<SignInManager<VlmsIdentityUser>>();
        var user = await users.FindByNameAsync(username);
        Assert.NotNull(user);
        return await signIn.CheckPasswordSignInAsync(user!, password, lockoutOnFailure: true);
    }

    private int IdentityUserCount()
    {
        using var db = NewContext();
        return db.Users.Count();
    }

    private int? AppUserIdOf(int studentId)
    {
        using var db = NewContext();
        return db.Students.Single(s => s.Id == studentId).AppUserId;
    }

    // --- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task LinkedParent_CreatesSignIn_WithNoEmail_StudentRole_AndLinkedRecord()
    {
        await CreateForAlexAsync("Alex.T");

        using var db = NewContext();
        var identity = await db.Users.SingleAsync(u => u.UserName == "Alex.T");
        Assert.Null(identity.Email);
        Assert.Null(identity.NormalizedEmail);
        Assert.False(identity.EmailConfirmed);

        var appUser = await db.AppUsers.SingleAsync(u => u.Id == identity.Id);
        Assert.Equal("", appUser.Email);
        Assert.Equal("Alex", appUser.DisplayName);

        Assert.Equal([Role.Student], await db.UserRoles.Where(r => r.UserId == identity.Id).Select(r => r.Role).ToListAsync());
        Assert.Equal(identity.Id, (await db.Students.SingleAsync(s => s.Id == Alex)).AppUserId);
    }

    [Fact]
    public async Task UnlinkedParent_IsRefused_AndNothingIsCreated()
    {
        using var parentB = As(ParentB, Role.Parent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => parentB.Service.CreateSignInAsync(Alex, "alex", Password));

        Assert.Equal(0, IdentityUserCount());
        Assert.Null(AppUserIdOf(Alex));
    }

    [Fact]
    public async Task ParentWhoHasLeft_IsRefused_EvenThoughLinked()
    {
        using var leaver = As(LeftParent, Role.Parent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => leaver.Service.CreateSignInAsync(Alex, "alex", Password));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => leaver.Service.ListForParentAsync());

        Assert.Equal(0, IdentityUserCount());
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Approver)]
    public async Task NonParentRoles_CannotCreate(Role role)
    {
        using var caller = As(ParentA, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.CreateSignInAsync(Alex, "alex", Password));

        Assert.Equal(0, IdentityUserCount());
    }

    [Fact]
    public async Task SecondSignIn_ForSameStudent_IsRefused()
    {
        await CreateForAlexAsync("alex");

        using var parent = As(ParentA, Role.Parent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => parent.Service.CreateSignInAsync(Alex, "alex2", Password));

        Assert.Equal(1, IdentityUserCount());
    }

    [Theory]
    [InlineData("alex")]
    [InlineData("ALEX")]
    public async Task DuplicateUsername_IsRefused_CaseInsensitively_AndLeavesNoRows(string duplicate)
    {
        await CreateForAlexAsync("alex");

        using var parentB = As(ParentB, Role.Parent);
        var result = await parentB.Service.CreateSignInAsync(Sam, duplicate, Password);

        Assert.False(result.Succeeded);
        Assert.Equal(1, IdentityUserCount());
        Assert.Null(AppUserIdOf(Sam));
        using var db = NewContext();
        Assert.Single(db.UserRoles.Where(r => r.Role == Role.Student));
    }

    [Theory]
    [InlineData("a@example.com")]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData(".dot")]
    [InlineData("")]
    public async Task InvalidOrEmailShapedUsername_IsRefused(string username)
    {
        using var parent = As(ParentA, Role.Parent);

        var result = await parent.Service.CreateSignInAsync(Alex, username, Password);

        Assert.False(result.Succeeded);
        Assert.Equal(0, IdentityUserCount());
    }

    [Fact]
    public async Task WeakPassword_IsRefused_AndLeavesNoRows()
    {
        using var parent = As(ParentA, Role.Parent);

        var result = await parent.Service.CreateSignInAsync(Alex, "alex", "weak");

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, IdentityUserCount());
        using var db = NewContext();
        Assert.Empty(db.AppUsers.Where(u => u.Email == ""));
        Assert.Null(AppUserIdOf(Alex));
    }

    [Fact]
    public async Task ListForParent_ShowsOnlyLinkedChildren_WithUsernames()
    {
        await CreateForAlexAsync("alex");

        using var parentA = As(ParentA, Role.Parent);
        var list = await parentA.Service.ListForParentAsync();

        var only = Assert.Single(list);
        Assert.Equal(new StudentSignInSummary(Alex, "Alex", "alex"), only);

        using var parentB = As(ParentB, Role.Parent);
        Assert.Equal([new StudentSignInSummary(Sam, "Sam", null)], await parentB.Service.ListForParentAsync());
    }

    // --- Reset --------------------------------------------------------------------------------

    [Fact]
    public async Task LinkedParent_ResetsPassword()
    {
        await CreateForAlexAsync("alex");

        using var parent = As(ParentA, Role.Parent);
        var result = await parent.Service.ResetPasswordAsync(Alex, NewPassword);

        Assert.True(result.Succeeded);
        Assert.True(await PasswordWorksAsync("alex", NewPassword));
        Assert.False(await PasswordWorksAsync("alex", Password));
    }

    [Fact]
    public async Task Admin_ResetsPassword_AndListsSignIns()
    {
        await CreateForAlexAsync("alex");

        using var admin = As(AdminUser, Role.Admin);
        Assert.Equal([new StudentSignInSummary(Alex, "Alex", "alex")], await admin.Service.ListWithSignInAsync());

        var result = await admin.Service.ResetPasswordAsync(Alex, NewPassword);

        Assert.True(result.Succeeded);
        Assert.True(await PasswordWorksAsync("alex", NewPassword));
    }

    [Fact]
    public async Task AdminWithoutMfa_CannotReset()
    {
        await CreateForAlexAsync("alex");

        using var admin = As(new FakeCurrentUserContext(AdminUser, hasMfaEnabled: false, Role.Admin));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.Service.ResetPasswordAsync(Alex, NewPassword));
        Assert.True(await PasswordWorksAsync("alex", Password));
    }

    [Theory]
    [InlineData(ParentB, Role.Parent)]
    [InlineData(LeftParent, Role.Parent)]
    [InlineData(TeacherUser, Role.Teacher)]
    [InlineData(TeacherUser, Role.SafeguardingOfficer)]
    [InlineData(TeacherUser, Role.Approver)]
    public async Task OthersCannotReset(int userId, Role role)
    {
        await CreateForAlexAsync("alex");

        using var caller = As(userId, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ResetPasswordAsync(Alex, NewPassword));
        Assert.True(await PasswordWorksAsync("alex", Password));
    }

    [Fact]
    public async Task StudentCannotResetOwnPassword()
    {
        await CreateForAlexAsync("alex");
        var alexUserId = AppUserIdOf(Alex)!.Value;

        using var student = As(alexUserId, Role.Student);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => student.Service.ResetPasswordAsync(Alex, NewPassword));
    }

    [Fact]
    public async Task Reset_WithWeakPassword_Fails_AndKeepsOldPassword()
    {
        await CreateForAlexAsync("alex");

        using var parent = As(ParentA, Role.Parent);
        var result = await parent.Service.ResetPasswordAsync(Alex, "weak");

        Assert.False(result.Succeeded);
        Assert.True(await PasswordWorksAsync("alex", Password));
    }

    [Fact]
    public async Task Reset_RefusesAnAccountWithAnEmail()
    {
        // Defence in depth: even if a student record pointed at an adult account, a parent can't
        // reset that adult's password through this route.
        using (var setup = Anonymous())
        {
            var provisioning = setup.Services.GetRequiredService<AppUserProvisioningService>();
            var (created, adult, _) = await provisioning.CreateAsync("adult@example.com", Password, "Adult", emailConfirmed: true);
            Assert.True(created.Succeeded);
            using var db = NewContext();
            await db.Students.Where(s => s.Id == Alex).ExecuteUpdateAsync(set => set.SetProperty(s => s.AppUserId, adult!.Id));
        }

        using var parent = As(ParentA, Role.Parent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => parent.Service.ResetPasswordAsync(Alex, NewPassword));
        Assert.True(await PasswordWorksAsync("adult@example.com", Password));
    }

    [Fact]
    public async Task Reset_ForStudentWithNoSignIn_Throws()
    {
        using var parent = As(ParentA, Role.Parent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => parent.Service.ResetPasswordAsync(Alex, NewPassword));
    }

    // --- Sign-in ------------------------------------------------------------------------------

    [Fact]
    public async Task Student_SignsInWithUsername_AndHasTheStudentRole()
    {
        await CreateForAlexAsync("alex");

        Assert.True((await SignInAsync("alex", Password)).Succeeded);
        Assert.False((await SignInAsync("alex", "Wr0ng!Passw0rd")).Succeeded);

        var alexUserId = AppUserIdOf(Alex)!.Value;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, alexUserId.ToString(System.Globalization.CultureInfo.InvariantCulture))], "Identity.Application"));
        var current = new IdentityCurrentUserContext(
            principal, new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options);

        Assert.Equal(alexUserId, current.UserId);
        Assert.True(current.HasRole(Role.Student));
        Assert.False(current.HasRole(Role.Parent));
    }

    [Fact]
    public async Task EmaillessAccount_NotLinkedToAStudent_CannotSignIn()
    {
        using (var setup = Anonymous())
        {
            var provisioning = setup.Services.GetRequiredService<AppUserProvisioningService>();
            var (created, _, _) = await provisioning.CreateStudentSignInAsync("orphan", Password, "Orphan");
            Assert.True(created.Succeeded);
        }

        Assert.True((await SignInAsync("orphan", Password)).IsNotAllowed);
    }

    [Fact]
    public async Task AdultConfirmationRule_IsUnchanged()
    {
        using (var setup = Anonymous())
        {
            var provisioning = setup.Services.GetRequiredService<AppUserProvisioningService>();
            Assert.True((await provisioning.CreateAsync("unconfirmed@example.com", Password, "U")).Result.Succeeded);
            Assert.True((await provisioning.CreateAsync("confirmed@example.com", Password, "C", emailConfirmed: true)).Result.Succeeded);
        }

        Assert.True((await SignInAsync("unconfirmed@example.com", Password)).IsNotAllowed);
        Assert.True((await SignInAsync("confirmed@example.com", Password)).Succeeded);
    }

    [Fact]
    public async Task AdultProvisioning_RefusesANonEmailUsername()
    {
        using var setup = Anonymous();
        var provisioning = setup.Services.GetRequiredService<AppUserProvisioningService>();

        await Assert.ThrowsAsync<ArgumentException>(() => provisioning.CreateAsync("alex", Password, "Not an email"));
        Assert.Equal(0, IdentityUserCount());
    }

    [Fact]
    public async Task ForgotPassword_DoesNotApplyToStudents()
    {
        await CreateForAlexAsync("alex");

        using var caller = Anonymous();
        var accountEmails = caller.Services.GetRequiredService<AccountEmailService>();
        var notifications = caller.Services.GetRequiredService<FakeNotificationService>();

        await accountEmails.RequestPasswordResetAsync("alex");
        var reset = await accountEmails.ResetPasswordAsync("alex", "any-code", NewPassword);

        Assert.Empty(notifications.SentRequests);
        Assert.True(reset.Succeeded); // the no-enumeration answer: nothing was changed
        Assert.True(await PasswordWorksAsync("alex", Password));
        Assert.False(await PasswordWorksAsync("alex", NewPassword));
    }

    [Fact]
    public void StudentUsernameRule_NeverAllowsAnAt()
    {
        Assert.True(StudentUsername.IsValid("alex_t-2.b"));
        Assert.False(StudentUsername.IsValid("alex@x"));
        Assert.False(StudentUsername.IsValid(new string('a', StudentUsername.MaxLength + 1)));
        Assert.True(StudentUsername.IsValid(new string('a', StudentUsername.MaxLength)));
    }

    // --- Password rules (adr/0007, Philip 22/10/2026) -----------------------------------------

    [Fact]
    public async Task StudentPassword_SevenCharactersRefused_EightLowercaseAccepted()
    {
        using var parent = As(ParentA, Role.Parent);

        var seven = await parent.Service.CreateSignInAsync(Alex, "alex", "abcdefg");
        Assert.False(seven.Succeeded);
        Assert.Equal(0, IdentityUserCount());

        var eight = await parent.Service.CreateSignInAsync(Alex, "alex", "abcdefgh");
        Assert.True(eight.Succeeded, string.Join(" ", eight.Errors));
        Assert.True(await PasswordWorksAsync("alex", "abcdefgh"));
    }

    [Fact]
    public async Task StudentReset_SevenCharactersRefused_EightLowercaseAccepted()
    {
        await CreateForAlexAsync("alex");

        using var parent = As(ParentA, Role.Parent);
        Assert.False((await parent.Service.ResetPasswordAsync(Alex, "abcdefg")).Succeeded);
        Assert.True(await PasswordWorksAsync("alex", Password));

        Assert.True((await parent.Service.ResetPasswordAsync(Alex, "abcdefgh")).Succeeded);
        Assert.True(await PasswordWorksAsync("alex", "abcdefgh"));
    }

    [Fact]
    public async Task StudentPassword_AllWhitespace_IsRefused()
    {
        using var parent = As(ParentA, Role.Parent);

        Assert.False((await parent.Service.CreateSignInAsync(Alex, "alex", "         ")).Succeeded);
    }

    [Fact]
    public void ExactlyOnePasswordValidator_IsRegistered_AndItIsTheVlmsOne()
    {
        using var caller = Anonymous();

        var validator = Assert.Single(caller.Services.GetServices<IPasswordValidator<VlmsIdentityUser>>());
        Assert.IsType<VlmsPasswordValidator>(validator);
    }

    // The adult rules are Identity's own validator and default options, unchanged: for an adult
    // account the VLMS validator gives exactly the stock validator's answer.
    [Theory]
    [InlineData("abcdefgh")]
    [InlineData("ABCDEFGH")]
    [InlineData("Abcdefgh")]
    [InlineData("Abcdefg1")]
    [InlineData("Ab1!x")]
    [InlineData("Abc1!x")]
    [InlineData("Str0ng!Passw0rd")]
    [InlineData("")]
    public async Task AdultPasswordRule_MatchesIdentitysDefaultValidatorExactly(string password)
    {
        using var caller = Anonymous();
        var users = caller.Services.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var adult = new VlmsIdentityUser { UserName = "adult@example.com", Email = "adult@example.com" };

        var expected = await new PasswordValidator<VlmsIdentityUser>().ValidateAsync(users, adult, password);
        var actual = await new VlmsPasswordValidator().ValidateAsync(users, adult, password);

        Assert.Equal(expected.Succeeded, actual.Succeeded);
        Assert.Equal(expected.Errors.Select(e => e.Code), actual.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task AdultRegistrationResetAndChange_StillEnforceTheAdultRules()
    {
        using var caller = Anonymous();
        var provisioning = caller.Services.GetRequiredService<AppUserProvisioningService>();
        var users = caller.Services.GetRequiredService<UserManager<VlmsIdentityUser>>();

        // Registration: an 8-character lower-case password is fine for a student, not an adult.
        var (weak, _, _) = await provisioning.CreateAsync("weak@example.com", "abcdefgh", "Weak");
        Assert.False(weak.Succeeded);
        Assert.Contains(weak.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordRequiresUpper));
        Assert.Contains(weak.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordRequiresDigit));
        Assert.Contains(weak.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric));

        var (created, adult, _) = await provisioning.CreateAsync("adult@example.com", Password, "Adult", emailConfirmed: true);
        Assert.True(created.Succeeded);

        // Reset (the adult email-link path ends in UserManager.ResetPasswordAsync).
        var token = await users.GeneratePasswordResetTokenAsync(adult!);
        Assert.False((await users.ResetPasswordAsync(adult!, token, "abcdefgh")).Succeeded);

        // Change.
        Assert.False((await users.ChangePasswordAsync(adult!, Password, "abcdefgh")).Succeeded);
        Assert.True((await users.ChangePasswordAsync(adult!, Password, NewPassword)).Succeeded);
        Assert.True(await PasswordWorksAsync("adult@example.com", NewPassword));
    }

    // --- Reset clears lockout -----------------------------------------------------------------

    [Theory]
    [InlineData(ParentA, Role.Parent)]
    [InlineData(AdminUser, Role.Admin)]
    public async Task Reset_ClearsLockout_AndFailedCount(int resetterId, Role resetterRole)
    {
        await CreateForAlexAsync("alex");
        for (var i = 0; i < VlmsIdentityPolicy.MaxFailedAccessAttempts; i++)
        {
            await SignInAsync("alex", "Wr0ng!Passw0rd");
        }

        Assert.True((await SignInAsync("alex", Password)).IsLockedOut);

        using (var resetter = As(resetterId, resetterRole))
        {
            Assert.True((await resetter.Service.ResetPasswordAsync(Alex, NewPassword)).Succeeded);
        }

        var account = await IdentityOfAsync(Alex);
        Assert.Null(account.LockoutEnd);
        Assert.Equal(0, account.AccessFailedCount);
        Assert.True((await SignInAsync("alex", NewPassword)).Succeeded);
    }

    [Fact]
    public async Task FailedReset_DoesNotClearLockout()
    {
        await CreateForAlexAsync("alex");
        for (var i = 0; i < VlmsIdentityPolicy.MaxFailedAccessAttempts; i++)
        {
            await SignInAsync("alex", "Wr0ng!Passw0rd");
        }

        using (var parent = As(ParentA, Role.Parent))
        {
            Assert.False((await parent.Service.ResetPasswordAsync(Alex, "short")).Succeeded);
        }

        Assert.NotNull((await IdentityOfAsync(Alex)).LockoutEnd);
    }

    // --- Disable and restore ------------------------------------------------------------------

    [Fact]
    public async Task DisabledSignIn_IsRefused_AndStaysDisabledAfterAParentReset_UntilRestored()
    {
        await CreateForAlexAsync("alex");

        using (var admin = As(AdminUser, Role.Admin))
        {
            await admin.Service.DisableSignInAsync(Alex);
            Assert.Equal([new StudentSignInSummary(Alex, "Alex", "alex", SignInDisabled: true)], await admin.Service.ListWithSignInAsync());
        }

        Assert.True((await SignInAsync("alex", Password)).IsNotAllowed);

        using (var parent = As(ParentA, Role.Parent))
        {
            Assert.True((await parent.Service.ResetPasswordAsync(Alex, NewPassword)).Succeeded);
            Assert.True(Assert.Single(await parent.Service.ListForParentAsync()).SignInDisabled);
        }

        Assert.True((await IdentityOfAsync(Alex)).SignInDisabled);
        Assert.True((await SignInAsync("alex", NewPassword)).IsNotAllowed);

        using (var admin = As(AdminUser, Role.Admin))
        {
            await admin.Service.RestoreSignInAsync(Alex);
        }

        Assert.True((await SignInAsync("alex", NewPassword)).Succeeded);
        using var parentAfter = As(ParentA, Role.Parent);
        Assert.False(Assert.Single(await parentAfter.Service.ListForParentAsync()).SignInDisabled);
    }

    [Fact]
    public async Task SecurityStamp_Changes_OnReset_Disable_AndRestore()
    {
        await CreateForAlexAsync("alex");
        var stamps = new List<string?> { (await IdentityOfAsync(Alex)).SecurityStamp };

        using (var parent = As(ParentA, Role.Parent))
        {
            Assert.True((await parent.Service.ResetPasswordAsync(Alex, NewPassword)).Succeeded);
        }

        stamps.Add((await IdentityOfAsync(Alex)).SecurityStamp);

        using (var admin = As(AdminUser, Role.Admin))
        {
            await admin.Service.DisableSignInAsync(Alex);
            stamps.Add((await IdentityOfAsync(Alex)).SecurityStamp);
            await admin.Service.RestoreSignInAsync(Alex);
            stamps.Add((await IdentityOfAsync(Alex)).SecurityStamp);
        }

        Assert.All(stamps, stamp => Assert.False(string.IsNullOrEmpty(stamp)));
        Assert.Equal(stamps.Count, stamps.Distinct().Count());
    }

    // The session check the cookie's SecurityStampValidator and the Blazor circuit's revalidation
    // both rely on: a principal issued before the disable no longer validates.
    [Fact]
    public async Task Disable_EndsAnExistingSession_AtTheNextStampCheck()
    {
        await CreateForAlexAsync("alex");

        ClaimsPrincipal principal;
        using (var caller = Anonymous())
        {
            var signIn = caller.Services.GetRequiredService<SignInManager<VlmsIdentityUser>>();
            var users = caller.Services.GetRequiredService<UserManager<VlmsIdentityUser>>();
            principal = await signIn.CreateUserPrincipalAsync((await users.FindByNameAsync("alex"))!);
            Assert.NotNull(await signIn.ValidateSecurityStampAsync(principal));
        }

        using (var admin = As(AdminUser, Role.Admin))
        {
            await admin.Service.DisableSignInAsync(Alex);
        }

        using var later = Anonymous();
        Assert.Null(await later.Services.GetRequiredService<SignInManager<VlmsIdentityUser>>().ValidateSecurityStampAsync(principal));
    }

    [Fact]
    public async Task DisableOrRestore_WhenAlreadyInThatState_LeavesTheStampAlone()
    {
        await CreateForAlexAsync("alex");
        var before = (await IdentityOfAsync(Alex)).SecurityStamp;

        using var admin = As(AdminUser, Role.Admin);
        await admin.Service.RestoreSignInAsync(Alex);

        Assert.Equal(before, (await IdentityOfAsync(Alex)).SecurityStamp);
    }

    [Theory]
    [InlineData(ParentA, Role.Parent)]
    [InlineData(TeacherUser, Role.Teacher)]
    [InlineData(TeacherUser, Role.SafeguardingOfficer)]
    public async Task OnlyAnAdminCanDisableOrRestore(int userId, Role role)
    {
        await CreateForAlexAsync("alex");

        using var caller = As(userId, role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.DisableSignInAsync(Alex));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.RestoreSignInAsync(Alex));

        Assert.False((await IdentityOfAsync(Alex)).SignInDisabled);
    }

    [Fact]
    public async Task AdminWithoutMfa_CannotDisable()
    {
        await CreateForAlexAsync("alex");

        using var admin = As(new FakeCurrentUserContext(AdminUser, hasMfaEnabled: false, Role.Admin));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => admin.Service.DisableSignInAsync(Alex));
        Assert.False((await IdentityOfAsync(Alex)).SignInDisabled);
    }

    [Fact]
    public async Task Disable_ForStudentWithNoSignIn_Throws()
    {
        using var admin = As(AdminUser, Role.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.Service.DisableSignInAsync(Alex));
    }

    [Fact]
    public async Task Leaving_DoesNotDisableTheSignIn()
    {
        await CreateForAlexAsync("alex");

        using (var admin = As(AdminUser, Role.Admin))
        {
            var leaving = new Vlms.Infrastructure.Retention.LeavingService(
                admin.Services.GetRequiredService<VlmsDbContext>(), admin.Services.GetRequiredService<ICurrentUserContext>());
            await leaving.MarkStudentInactiveAsync(Alex);
        }

        using (var db = NewContext())
        {
            Assert.Equal(StudentStatus.Inactive, db.Students.Single(s => s.Id == Alex).Status);
        }

        Assert.False((await IdentityOfAsync(Alex)).SignInDisabled);
        Assert.True((await SignInAsync("alex", Password)).Succeeded);
    }

    // --- One parent, several children (Philip, 22/10/2026) ------------------------------------

    [Fact]
    public async Task ParentOfThree_CreatesTwo_ResetsOne_AndTheThirdStillShowsCreate()
    {
        using (var parent = As(ParentC, Role.Parent))
        {
            Assert.True((await parent.Service.CreateSignInAsync(Ivy, "ivy", "ivypassword")).Succeeded);
            Assert.True((await parent.Service.CreateSignInAsync(Jo, "jo.c", "jopassword")).Succeeded);
            Assert.True((await parent.Service.ResetPasswordAsync(Ivy, "ivy-new-password")).Succeeded);

            Assert.Equal(
                [
                    new StudentSignInSummary(Ivy, "Ivy", "ivy"),
                    new StudentSignInSummary(Jo, "Jo", "jo.c"),
                    new StudentSignInSummary(Kim, "Kim", null)
                ],
                await parent.Service.ListForParentAsync());
        }

        Assert.True(await PasswordWorksAsync("ivy", "ivy-new-password"));
        Assert.False(await PasswordWorksAsync("ivy", "ivypassword"));
        Assert.True(await PasswordWorksAsync("jo.c", "jopassword"));
        Assert.Null(AppUserIdOf(Kim));

        // The parent of three can't reach another family's child.
        using var parentC = As(ParentC, Role.Parent);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => parentC.Service.CreateSignInAsync(Sam, "sam", "sampassword"));
    }

    // --- Stay signed in (VlmsSignInManager) ---------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudentSignIn_AlwaysIssuesAPersistentNinetyDayCookie(bool rememberMe)
    {
        await CreateForAlexAsync("alex");

        var properties = await CookieSignInAsync("alex", Password, rememberMe);

        Assert.True(properties.IsPersistent);
        Assert.NotNull(properties.ExpiresUtc);
        var lifetime = properties.ExpiresUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(lifetime, StudentSignInPolicy.PersistentSignInLifetime - TimeSpan.FromMinutes(5), StudentSignInPolicy.PersistentSignInLifetime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdultSignIn_KeepsTheirRememberMeChoice_AndTheDefaultLifetime(bool rememberMe)
    {
        using (var setup = Anonymous())
        {
            var provisioning = setup.Services.GetRequiredService<AppUserProvisioningService>();
            Assert.True((await provisioning.CreateAsync("adult@example.com", Password, "Adult", emailConfirmed: true)).Result.Succeeded);
        }

        var properties = await CookieSignInAsync("adult@example.com", Password, rememberMe);

        Assert.Equal(rememberMe, properties.IsPersistent);
        Assert.Null(properties.ExpiresUtc); // left to the cookie handler's configured default
    }

    private async Task<AuthenticationProperties> CookieSignInAsync(string username, string password, bool rememberMe)
    {
        var recorder = new RecordingAuthenticationService();
        using var provider = TestIdentityServices.BuildServiceProvider(_connectionString, new FakeCurrentUserContext(null), configure: services =>
        {
            services.Configure<IdentityOptions>(VlmsIdentityPolicy.Apply);
            services.AddAuthentication();
            services.AddSingleton<IAuthenticationService>(recorder);
            services.AddHttpContextAccessor();
            new IdentityBuilder(typeof(VlmsIdentityUser), services)
                .AddSignInManager<VlmsSignInManager>()
                .AddUserConfirmation<VlmsUserConfirmation>();
        });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<VlmsIdentityUser>>();
        Assert.IsType<VlmsSignInManager>(signIn);
        var result = await signIn.PasswordSignInAsync(username, password, rememberMe, lockoutOnFailure: true);
        Assert.True(result.Succeeded);

        var (scheme, properties) = Assert.Single(recorder.SignIns);
        Assert.Equal(IdentityConstants.ApplicationScheme, scheme);
        return properties;
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<(string? Scheme, AuthenticationProperties Properties)> SignIns { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignIns.Add((scheme, properties ?? new AuthenticationProperties()));
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private async Task<VlmsIdentityUser> IdentityOfAsync(int studentId)
    {
        using var db = NewContext();
        var appUserId = db.Students.Single(s => s.Id == studentId).AppUserId!.Value;
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == appUserId);
    }
}
