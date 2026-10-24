using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="AccountEmailService"/>: confirmation and reset links are emailed, codes
/// work once, nothing is sent for unknown or unconfirmed addresses (no enumeration, no takeover of
/// a squatted account), and <see cref="VlmsIdentityPolicy"/> sets the policy it documents.
/// </summary>
public sealed class AccountEmailServiceTests : IDisposable
{
    private const string Password = "Str0ng!Passw0rd";
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;

    public AccountEmailServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-accountemail-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schemaContext = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options,
            TestSeedUserContext.Instance);
        schemaContext.Database.EnsureCreated();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class Fixture(ServiceProvider provider, IServiceScope scope) : IDisposable
    {
        public AccountEmailService Service { get; } = scope.ServiceProvider.GetRequiredService<AccountEmailService>();
        public AppUserProvisioningService Provisioning { get; } = scope.ServiceProvider.GetRequiredService<AppUserProvisioningService>();
        public UserManager<VlmsIdentityUser> Users { get; } = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        public FakeNotificationService Notifications { get; } = scope.ServiceProvider.GetRequiredService<FakeNotificationService>();

        public async Task<VlmsIdentityUser> CreateAsync(string email, bool confirmed)
        {
            var (result, user, _) = await Provisioning.CreateAsync(email, Password, "Test User", emailConfirmed: confirmed);
            Assert.True(result.Succeeded);
            return user!;
        }

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Fixture Build()
    {
        var provider = TestIdentityServices.BuildServiceProvider(_connectionString, new FakeCurrentUserContext(null));
        return new Fixture(provider, provider.CreateScope());
    }

    private static string QueryValue(string body, string name) =>
        Uri.UnescapeDataString(Regex.Match(body, $@"[?&]{name}=([^&\s]+)").Groups[1].Value);

    [Fact]
    public async Task SendConfirmationAsync_EmailsALink_ThatConfirmsTheAccountOnce()
    {
        using var fixture = Build();
        var user = await fixture.CreateAsync("new@example.com", confirmed: false);

        await fixture.Service.SendConfirmationAsync(user);

        var email = Assert.Single(fixture.Notifications.SentRequests);
        Assert.Equal("new@example.com", email.RecipientEmail);
        Assert.Contains("https://test.example/Account/ConfirmEmail?userId=", email.Body);
        Assert.Contains("2 hours", email.Body);

        Assert.True(await fixture.Service.ConfirmEmailAsync(int.Parse(QueryValue(email.Body, "userId")), QueryValue(email.Body, "code")));
        Assert.True(await fixture.Users.IsEmailConfirmedAsync(await fixture.Users.FindByIdAsync(user.Id.ToString()) ?? throw new InvalidOperationException()));
    }

    [Fact]
    public async Task ConfirmEmailAsync_WrongCodeOrUnknownUser_ReturnsFalse()
    {
        using var fixture = Build();
        var user = await fixture.CreateAsync("new@example.com", confirmed: false);

        Assert.False(await fixture.Service.ConfirmEmailAsync(user.Id, "not-a-real-code"));
        Assert.False(await fixture.Service.ConfirmEmailAsync(user.Id, ""));
        Assert.False(await fixture.Service.ConfirmEmailAsync(99999, "anything"));
        Assert.False(await fixture.Users.IsEmailConfirmedAsync(user));
    }

    [Fact]
    public async Task RequestPasswordResetAsync_ConfirmedAccount_EmailsALink_ThatSetsTheNewPassword()
    {
        using var fixture = Build();
        await fixture.CreateAsync("member@example.com", confirmed: true);

        await fixture.Service.RequestPasswordResetAsync("  MEMBER@example.com ");

        var email = Assert.Single(fixture.Notifications.SentRequests);
        Assert.Contains("https://test.example/Account/ResetPassword?code=", email.Body);

        var result = await fixture.Service.ResetPasswordAsync("member@example.com", QueryValue(email.Body, "code"), "N3w!Passw0rd!");

        Assert.True(result.Succeeded);
        var user = await fixture.Users.FindByEmailAsync("member@example.com") ?? throw new InvalidOperationException();
        Assert.True(await fixture.Users.CheckPasswordAsync(user, "N3w!Passw0rd!"));
        Assert.False(await fixture.Users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task ResetPasswordAsync_SameCodeTwice_SecondAttemptFails()
    {
        using var fixture = Build();
        await fixture.CreateAsync("member@example.com", confirmed: true);
        await fixture.Service.RequestPasswordResetAsync("member@example.com");
        var code = QueryValue(fixture.Notifications.SentRequests.Single().Body, "code");

        Assert.True((await fixture.Service.ResetPasswordAsync("member@example.com", code, "N3w!Passw0rd!")).Succeeded);
        Assert.False((await fixture.Service.ResetPasswordAsync("member@example.com", code, "Th1rd!Passw0rd")).Succeeded);
    }

    [Fact]
    public async Task ResetPasswordAsync_InvalidCode_Fails_AndKeepsTheOldPassword()
    {
        using var fixture = Build();
        var user = await fixture.CreateAsync("member@example.com", confirmed: true);

        Assert.False((await fixture.Service.ResetPasswordAsync("member@example.com", "forged", "N3w!Passw0rd!")).Succeeded);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task RequestPasswordResetAsync_UnconfirmedAccount_SendsNothing()
    {
        using var fixture = Build();
        await fixture.CreateAsync("squatted@example.com", confirmed: false);

        await fixture.Service.RequestPasswordResetAsync("squatted@example.com");

        Assert.Empty(fixture.Notifications.SentRequests);
    }

    [Fact]
    public async Task UnknownEmail_RequestAndResetBehaveLikeSuccess_AndSendNothing()
    {
        using var fixture = Build();

        await fixture.Service.RequestPasswordResetAsync("nobody@example.com");
        await fixture.Service.ResendConfirmationAsync("nobody@example.com");
        var result = await fixture.Service.ResetPasswordAsync("nobody@example.com", "code", "N3w!Passw0rd!");

        Assert.True(result.Succeeded);
        Assert.Empty(fixture.Notifications.SentRequests);
    }

    [Fact]
    public async Task ResendConfirmationAsync_SendsOnlyForUnconfirmedAccounts()
    {
        using var fixture = Build();
        await fixture.CreateAsync("pending@example.com", confirmed: false);
        await fixture.CreateAsync("done@example.com", confirmed: true);

        await fixture.Service.ResendConfirmationAsync("done@example.com");
        await fixture.Service.ResendConfirmationAsync("pending@example.com");

        var email = Assert.Single(fixture.Notifications.SentRequests);
        Assert.Equal("pending@example.com", email.RecipientEmail);
    }

    [Fact]
    public void VlmsIdentityPolicy_RequiresConfirmedAccounts_AndSetsLockout()
    {
        var options = new IdentityOptions();

        VlmsIdentityPolicy.Apply(options);

        Assert.True(options.SignIn.RequireConfirmedAccount);
        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
        Assert.Equal(TimeSpan.FromHours(2), VlmsIdentityPolicy.EmailTokenLifespan);
    }
}
