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
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
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
}
