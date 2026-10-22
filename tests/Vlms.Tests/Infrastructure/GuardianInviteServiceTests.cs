using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies the self-rolled guardian invite flow (STATE.md "Identity" item 5, replacing the former
/// Microsoft Graph guardian-invitation blocker) — token generation, single-use/expiry enforcement,
/// correct <see cref="ParentGuardian.AppUserId"/> assignment on acceptance, and denial of a
/// reused/expired/tampered token. Real DI wiring for <see cref="AppUserProvisioningService"/>'s
/// <c>UserManager&lt;VlmsIdentityUser&gt;</c> dependency — same reasoning as
/// <see cref="AppUserProvisioningServiceTests"/>.
/// </summary>
public sealed class GuardianInviteServiceTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    public GuardianInviteServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-guardianinvite-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schemaContext = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);
        schemaContext.Database.EnsureCreated();

        using var seed = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            NullCurrentUserContext.Instance);
        seed.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        seed.AppUsers.Add(new AppUser { Id = 1000, DisplayName = "Admin One", Email = "admin@example.com" });
        seed.AppUsers.Add(new AppUser { Id = 1099, DisplayName = "Parent One", Email = "parent1@example.com" });
        seed.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Jane Parent", ContactInfo = "jane@example.com", IsPrimary = true });
        seed.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Already Registered Parent", ContactInfo = "registered@example.com", IsPrimary = true, AppUserId = 1099 });
        // A Teacher who is also a parent: the invite's address differs in case from the account's.
        seed.ParentGuardians.Add(new ParentGuardian { Id = 3, Name = "Tom Teacher", ContactInfo = "Tom.Teacher@Example.com", IsPrimary = true });
        seed.Students.Add(new Student
        {
            Id = 100,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2012, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        seed.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class Fixture(ServiceProvider provider, IServiceScope scope) : IDisposable
    {
        public VlmsDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();
        public GuardianInviteService InviteService { get; } = scope.ServiceProvider.GetRequiredService<GuardianInviteService>();
        public FakeNotificationService Notifications { get; } = scope.ServiceProvider.GetRequiredService<FakeNotificationService>();
        public AppUserProvisioningService Provisioning { get; } = scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Fixture Build(int? userId, TimeSpan? inviteLifetime = null, params Role[] roles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(userId, roles));
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        // One key ring for the whole test class, so a token issued by one caller's fixture can be
        // accepted through another's (the existing-account tests sign in as different users).
        services.AddSingleton(_dataProtection);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://test.example" })
            .Build());
        services.AddSingleton<FakeNotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<FakeNotificationService>());
        services.AddIdentityCore<VlmsIdentityUser>().AddEntityFrameworkStores<VlmsDbContext>();
        services.AddScoped<AppUserProvisioningService>();
        services.AddScoped(sp => new GuardianInviteService(
            sp.GetRequiredService<VlmsDbContext>(),
            sp.GetRequiredService<ICurrentUserContext>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<AppUserProvisioningService>(),
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<IConfiguration>(),
            inviteLifetime));

        var provider = services.BuildServiceProvider();
        return new Fixture(provider, provider.CreateScope());
    }

    [Fact]
    public async Task IssueAsync_ByAdmin_CreatesInvite_AndSendsAnEmail()
    {
        using var fixture = Build(1000, null, Role.Admin);

        var (invite, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        Assert.Equal(100, invite.StudentId);
        Assert.Equal(1, invite.ParentGuardianId);
        Assert.Equal(1000, invite.CreatedByUserId);
        Assert.Null(invite.ConsumedAt);
        Assert.False(string.IsNullOrEmpty(token));

        var sent = Assert.Single(fixture.Notifications.SentRequests);
        Assert.Equal("jane@example.com", sent.RecipientEmail);
        Assert.Equal(NotificationPriority.Standard, sent.Priority);
        Assert.Contains(token, sent.Body);
    }

    [Fact]
    public async Task IssueAsync_ByParent_Throws_AndCreatesNoInvite()
    {
        using var fixture = Build(1099, null, Role.Parent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1));

        Assert.Empty(fixture.Db.GuardianInvites);
        Assert.Empty(fixture.Notifications.SentRequests);
    }

    [Fact]
    public async Task AcceptAsync_WithAValidToken_CreatesAccount_LinksGuardian_AndMarksInviteConsumed()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (invite, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        var appUser = await fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd");

        Assert.Equal("jane@example.com", appUser.Email);
        Assert.Equal("Jane Parent", appUser.DisplayName);

        var guardian = await fixture.Db.ParentGuardians.SingleAsync(g => g.Id == 1);
        Assert.Equal(appUser.Id, guardian.AppUserId);

        var persistedInvite = await fixture.Db.GuardianInvites.SingleAsync(i => i.Id == invite.Id);
        Assert.NotNull(persistedInvite.ConsumedAt);
        Assert.Equal(appUser.Id, persistedInvite.ConsumedByAppUserId);

        // Deny-by-default preserved through this path too.
        Assert.Empty(fixture.Db.UserRoles.Where(r => r.UserId == appUser.Id));
    }

    [Fact]
    public async Task AcceptAsync_ConfirmsTheEmail_BecauseTheInviteWasSentToIt()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (_, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        var appUser = await fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd");

        Assert.True((await fixture.Db.Users.SingleAsync(u => u.Id == appUser.Id)).EmailConfirmed);
    }

    [Fact]
    public async Task AcceptAsync_SameTokenTwice_SecondAttemptThrows_AndDoesNotCreateASecondAccount()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (_, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        var firstAppUser = await fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InviteService.AcceptAsync(token, "AnotherStr0ng!Pass"));
        Assert.Contains("already been used", ex.Message);

        var accounts = await fixture.Db.AppUsers.Where(u => u.Email == "jane@example.com").ToListAsync();
        var singleAccount = Assert.Single(accounts);
        Assert.Equal(firstAppUser.Id, singleAccount.Id);
    }

    [Fact]
    public async Task AcceptAsync_AfterDatabaseExpiry_Throws_AndCreatesNoAccount()
    {
        // Belt-and-braces expiry check against the row itself (GuardianInvite's own doc comment) —
        // exercised independently of Data Protection's own built-in expiry window by editing the
        // persisted ExpiresAt directly, so the token itself is still cryptographically valid when
        // unprotected (proving this specific check fires, not just relying on the token expiring).
        using var fixture = Build(1000, null, Role.Admin);
        var (invite, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        var toExpire = await fixture.Db.GuardianInvites.SingleAsync(i => i.Id == invite.Id);
        fixture.Db.Entry(toExpire).CurrentValues.SetValues(new GuardianInvite
        {
            Id = toExpire.Id,
            StudentId = toExpire.StudentId,
            ParentGuardianId = toExpire.ParentGuardianId,
            CreatedByUserId = toExpire.CreatedByUserId,
            CreatedAt = toExpire.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            ConsumedAt = null,
            ConsumedByAppUserId = null
        });
        await fixture.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd"));
        Assert.Contains("expired", ex.Message);

        Assert.Empty(fixture.Db.AppUsers.Where(u => u.Email == "jane@example.com"));
    }

    [Fact]
    public async Task AcceptAsync_AfterTheTokensOwnExpiryWindowPasses_ThrowsCryptographicException()
    {
        // Data Protection's own built-in expiry (ITimeLimitedDataProtector.Unprotect throws
        // CryptographicException for an expired payload — verified against Microsoft Learn, not
        // assumed, per GuardianInviteService's doc comment), exercised independently of the
        // database-level check above via a deliberately tiny lifetime.
        using var fixture = Build(1000, TimeSpan.FromMilliseconds(50), Role.Admin);
        var (_, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<CryptographicException>(
            () => fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd"));

        Assert.Empty(fixture.Db.AppUsers.Where(u => u.Email == "jane@example.com"));
    }

    [Fact]
    public async Task AcceptAsync_WithATamperedToken_ThrowsCryptographicException()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (_, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        await Assert.ThrowsAsync<CryptographicException>(
            () => fixture.InviteService.AcceptAsync(tampered, "Str0ng!Passw0rd"));
    }

    [Fact]
    public async Task AcceptAsync_ForAGuardianWhoAlreadyHasAnAccount_Throws()
    {
        // Genuinely shouldn't happen in normal use (GuardianLinkService only issues an invite when
        // AppUserId is null), but a stale second invite for an already-registered guardian must
        // still be rejected — see GuardianInviteService.AcceptAsync's own doc comment.
        using var fixture = Build(1000, null, Role.Admin);
        var (_, token) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InviteService.AcceptAsync(token, "Str0ng!Passw0rd"));
    }

    // --- Invite to an email that already has an account (functional.md FR-004, 22/10/2026):
    // the invitee signs in to that account to accept; ownership is proved by signing in, never by
    // the email match alone. ------------------------------------------------------------------------

    /// <summary>Creates an adult account (Identity user + AppUser) holding <paramref name="roles"/>; returns its id.</summary>
    private async Task<int> CreateAccountAsync(string email, params Role[] roles)
    {
        using var fixture = Build(null, null);
        var (result, _, appUser) = await fixture.Provisioning.CreateAsync(email, "Str0ng!Passw0rd", email, emailConfirmed: true);
        Assert.True(result.Succeeded);
        foreach (var role in roles)
        {
            fixture.Db.UserRoles.Add(new UserRole { UserId = appUser!.Id, Role = role });
        }

        await fixture.Db.SaveChangesAsync();
        return appUser!.Id;
    }

    private async Task<string> IssueForTomAsync()
    {
        using var admin = Build(1000, null, Role.Admin);
        var (_, token) = await admin.InviteService.IssueAsync(studentId: 100, parentGuardianId: 3);
        return token;
    }

    private async Task<(int? AppUserId, int? ConsumedBy)> TomStateAsync()
    {
        using var fixture = Build(null, null);
        var guardian = await fixture.Db.ParentGuardians.AsNoTracking().SingleAsync(g => g.Id == 3);
        var invite = await fixture.Db.GuardianInvites.AsNoTracking().SingleAsync(i => i.ParentGuardianId == 3);
        return (guardian.AppUserId, invite.ConsumedByAppUserId);
    }

    [Fact]
    public async Task GetAcceptModeAsync_NoAccountForTheEmail_IsCreateAccount()
    {
        var token = await IssueForTomAsync();
        using var anonymous = Build(null, null);

        Assert.Equal(GuardianInviteService.AcceptMode.CreateAccount, await anonymous.InviteService.GetAcceptModeAsync(token));
    }

    [Fact]
    public async Task ExistingAccount_NotSignedIn_IsToldToSignIn_AndCanNeitherCreateNorLink()
    {
        await CreateAccountAsync("tom.teacher@example.com", Role.Teacher);
        var token = await IssueForTomAsync();
        using var anonymous = Build(null, null);

        Assert.Equal(GuardianInviteService.AcceptMode.SignInRequired, await anonymous.InviteService.GetAcceptModeAsync(token));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => anonymous.InviteService.AcceptAsync(token, "An0ther!Passw0rd"));
        Assert.Equal(GuardianInviteService.SignInToAcceptMessage, ex.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonymous.InviteService.AcceptForSignedInUserAsync(token));

        Assert.Single(anonymous.Db.Users.Where(u => u.NormalizedEmail == "TOM.TEACHER@EXAMPLE.COM"));
        Assert.Equal((null, null), await TomStateAsync());
    }

    [Fact]
    public async Task ExistingAccount_SignedInAsThatAccount_LinksIt_ConsumesTheInvite_AndKeepsItsRoles()
    {
        var tomId = await CreateAccountAsync("tom.teacher@example.com", Role.Teacher);
        var token = await IssueForTomAsync();
        using var tom = Build(tomId, null, Role.Teacher);

        Assert.Equal(GuardianInviteService.AcceptMode.ReadyToLink, await tom.InviteService.GetAcceptModeAsync(token));
        var linked = await tom.InviteService.AcceptForSignedInUserAsync(token);

        Assert.Equal(tomId, linked.Id);
        Assert.Equal((tomId, tomId), await TomStateAsync());
        // Accepting grants no role: Parent still comes from an Admin.
        Assert.Equal([Role.Teacher], tom.Db.UserRoles.Where(r => r.UserId == tomId).Select(r => r.Role).ToList());
        // Single use, in this path too.
        var reused = await Assert.ThrowsAsync<InvalidOperationException>(() => tom.InviteService.AcceptForSignedInUserAsync(token));
        Assert.Contains("already been used", reused.Message);
    }

    [Fact]
    public async Task ExistingAccount_SignedInAsADifferentAccount_IsRefused_WithoutNamingTheInvitedAccount()
    {
        await CreateAccountAsync("tom.teacher@example.com", Role.Teacher);
        var otherId = await CreateAccountAsync("someone.else@example.com", Role.Teacher);
        var token = await IssueForTomAsync();
        using var other = Build(otherId, null, Role.Teacher);

        Assert.Equal(GuardianInviteService.AcceptMode.SignedInAsDifferentAccount, await other.InviteService.GetAcceptModeAsync(token));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => other.InviteService.AcceptForSignedInUserAsync(token));

        Assert.Equal(GuardianInviteService.DifferentAccountMessage, ex.Message);
        Assert.DoesNotContain("tom", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((null, null), await TomStateAsync());
    }

    [Fact]
    public async Task NoAccountForTheEmail_SignedInAsSomeoneElse_CannotLinkTheirOwnAccount()
    {
        // Email match is required as well as sign-in: a signed-in user can't claim an invite sent to
        // an address that has no account by linking their own account to it.
        var otherId = await CreateAccountAsync("someone.else@example.com", Role.Teacher);
        var token = await IssueForTomAsync();
        using var other = Build(otherId, null, Role.Teacher);

        Assert.Equal(GuardianInviteService.AcceptMode.CreateAccount, await other.InviteService.GetAcceptModeAsync(token));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => other.InviteService.AcceptForSignedInUserAsync(token));

        Assert.Equal(GuardianInviteService.DifferentAccountMessage, ex.Message);
        Assert.Equal((null, null), await TomStateAsync());
    }

    [Fact]
    public async Task ExistingAccount_ExpiredOrTamperedToken_IsRefused()
    {
        var tomId = await CreateAccountAsync("tom.teacher@example.com", Role.Teacher);
        var token = await IssueForTomAsync();
        using var tom = Build(tomId, null, Role.Teacher);

        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        await Assert.ThrowsAsync<CryptographicException>(() => tom.InviteService.GetAcceptModeAsync(tampered));
        await Assert.ThrowsAsync<CryptographicException>(() => tom.InviteService.AcceptForSignedInUserAsync(tampered));

        var invite = await tom.Db.GuardianInvites.SingleAsync(i => i.ParentGuardianId == 3);
        tom.Db.Entry(invite).CurrentValues.SetValues(new GuardianInvite
        {
            Id = invite.Id, StudentId = invite.StudentId, ParentGuardianId = invite.ParentGuardianId,
            CreatedByUserId = invite.CreatedByUserId, CreatedAt = invite.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await tom.Db.SaveChangesAsync();

        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() => tom.InviteService.AcceptForSignedInUserAsync(token));
        Assert.Contains("expired", expired.Message);
        Assert.Equal((null, null), await TomStateAsync());
    }

    [Fact]
    public async Task GuardianAlreadyLinked_IsNeverRelinked_EvenBySignedInOwnerOfTheEmail()
    {
        // Guardian 2 is already linked to AppUser 1099. An account that owns guardian 2's address
        // signs in with a still-valid invite: refused, and the link is unchanged.
        var ownerId = await CreateAccountAsync("registered@example.com");
        string token;
        using (var admin = Build(1000, null, Role.Admin))
        {
            (_, token) = await admin.InviteService.IssueAsync(studentId: 100, parentGuardianId: 2);
        }

        using var owner = Build(ownerId, null);
        var mode = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.InviteService.GetAcceptModeAsync(token));
        Assert.Equal(GuardianInviteService.AlreadyLinkedMessage, mode.Message);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.InviteService.AcceptForSignedInUserAsync(token));
        Assert.Equal(GuardianInviteService.AlreadyLinkedMessage, ex.Message);

        Assert.Equal(1099, (await owner.Db.ParentGuardians.AsNoTracking().SingleAsync(g => g.Id == 2)).AppUserId);
    }

    // --- Resend (functional.md FR-018, TC-036) ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResendAsync_ByTeacherOrParent_Throws_AndChangesNothing(bool teacher)
    {
        using var issuer = Build(1000, null, Role.Admin);
        var (invite, _) = await issuer.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);
        issuer.Notifications.SentRequests.Clear();

        using var fixture = Build(teacher ? 1000 : 1099, null, teacher ? Role.Teacher : Role.Parent);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InviteService.ResendAsync(1));

        Assert.Single(fixture.Db.GuardianInvites);
        Assert.Empty(fixture.Notifications.SentRequests);
    }

    [Fact]
    public async Task ResendAsync_ForAGuardianWithAnAccount_IsRefused_AndSendsNothing()
    {
        using var fixture = Build(1000, null, Role.Admin);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InviteService.ResendAsync(2));

        Assert.Equal(GuardianInviteService.ResendAlreadyLinkedMessage, ex.Message);
        Assert.Contains("sign in", ex.Message);
        Assert.Empty(fixture.Db.GuardianInvites);
        Assert.Empty(fixture.Notifications.SentRequests);
    }

    [Fact]
    public async Task ResendAsync_ByAdmin_StopsTheOldTokenWorking_AndTheNewOneWorks_AndEmailsTheGuardian()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (_, oldToken) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);
        fixture.Notifications.SentRequests.Clear();

        await fixture.InviteService.ResendAsync(1);

        var sent = Assert.Single(fixture.Notifications.SentRequests);
        Assert.Equal("jane@example.com", sent.RecipientEmail);
        var newToken = System.Text.RegularExpressions.Regex.Match(sent.Body, @"token=(\S+)").Groups[1].Value;
        newToken = Uri.UnescapeDataString(newToken);
        Assert.False(string.IsNullOrEmpty(newToken));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InviteService.AcceptAsync(oldToken, "Str0ng!Passw0rd"));
        Assert.Contains("expired", ex.Message);
        Assert.Empty(fixture.Db.AppUsers.Where(u => u.Email == "jane@example.com"));

        var appUser = await fixture.InviteService.AcceptAsync(newToken, "Str0ng!Passw0rd");
        Assert.Equal("jane@example.com", appUser.Email);
        Assert.Equal(appUser.Id, (await fixture.Db.ParentGuardians.SingleAsync(g => g.Id == 1)).AppUserId);
    }

    [Fact]
    public async Task ResendAsync_IssuesAFreshInvite_WithTheNormalLifetime_ForTheSameStudent()
    {
        using var fixture = Build(1000, null, Role.Admin);
        await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);

        await fixture.InviteService.ResendAsync(1);

        var invites = await fixture.Db.GuardianInvites.ToListAsync();
        Assert.Equal(2, invites.Count);
        var now = DateTimeOffset.UtcNow;
        Assert.Single(invites, i => i.ExpiresAt > now);
        var fresh = invites.Single(i => i.ExpiresAt > now);
        Assert.Equal(100, fresh.StudentId);
        Assert.True(fresh.ExpiresAt > now.Add(GuardianInviteService.DefaultInviteLifetime).AddMinutes(-1));
    }

    [Fact]
    public async Task ResendAsync_WhenTheEarlierInviteHasExpired_StillIssuesANewWorkingInvite()
    {
        using var fixture = Build(1000, null, Role.Admin);
        var (invite, _) = await fixture.InviteService.IssueAsync(studentId: 100, parentGuardianId: 1);
        var row = await fixture.Db.GuardianInvites.SingleAsync(i => i.Id == invite.Id);
        fixture.Db.Entry(row).CurrentValues.SetValues(new GuardianInvite
        {
            Id = row.Id, StudentId = row.StudentId, ParentGuardianId = row.ParentGuardianId,
            CreatedByUserId = row.CreatedByUserId, CreatedAt = row.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Notifications.SentRequests.Clear();

        await fixture.InviteService.ResendAsync(1);

        Assert.Single(fixture.Notifications.SentRequests);
        Assert.Equal(2, await fixture.Db.GuardianInvites.CountAsync());
    }
}
