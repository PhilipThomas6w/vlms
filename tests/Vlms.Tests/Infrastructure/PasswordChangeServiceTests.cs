using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// An adult changing their own password while signed in (FR-011, adr/0007 decision 1, TC-030).
/// Real Identity wiring via <see cref="TestIdentityServices"/> with the real password rules.
/// </summary>
public sealed class PasswordChangeServiceTests : IDisposable
{
    private const string Current = "Str0ng!Passw0rd";
    private const string Next = "N3w!Passw0rdX";

    private readonly SqliteConnection _anchor;
    private readonly ServiceProvider _provider;

    public PasswordChangeServiceTests()
    {
        var cs = $"Data Source=file:vlms-pwchange-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(cs);
        _anchor.Open();
        _provider = TestIdentityServices.BuildServiceProvider(
            cs, new FakeCurrentUserContext(1, Role.Admin), configure: s => s.AddScoped<PasswordChangeService>());
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<VlmsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _anchor.Dispose();
    }

    [Fact]
    public async Task Right_current_password_changes_it_and_rotates_the_security_stamp()
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await CreateAdultAsync(users);
        var stampBefore = await users.GetSecurityStampAsync(user);

        var result = await scope.ServiceProvider.GetRequiredService<PasswordChangeService>().ChangeAsync(user, Current, Next);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        Assert.True(await users.CheckPasswordAsync(user, Next));
        Assert.False(await users.CheckPasswordAsync(user, Current));
        Assert.NotEqual(stampBefore, await users.GetSecurityStampAsync(user));
    }

    [Fact]
    public async Task Wrong_current_password_fails_and_leaves_the_password_and_stamp_unchanged()
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await CreateAdultAsync(users);
        var stampBefore = await users.GetSecurityStampAsync(user);

        var result = await scope.ServiceProvider.GetRequiredService<PasswordChangeService>().ChangeAsync(user, "Wrong!Passw0rd1", Next);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "PasswordMismatch");
        Assert.True(await users.CheckPasswordAsync(user, Current));
        Assert.Equal(stampBefore, await users.GetSecurityStampAsync(user));
    }

    [Theory]
    [InlineData("Sh0r!")]
    [InlineData("alllowercase1!")]
    [InlineData("NoDigits!!")]
    [InlineData("NoSymbol123")]
    public async Task Weak_new_password_is_rejected_by_the_adult_rule(string weak)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await CreateAdultAsync(users);

        var result = await scope.ServiceProvider.GetRequiredService<PasswordChangeService>().ChangeAsync(user, Current, weak);

        Assert.False(result.Succeeded);
        Assert.True(await users.CheckPasswordAsync(user, Current));
    }

    [Fact]
    public async Task Student_account_is_refused_and_nothing_changes()
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var student = new VlmsIdentityUser { Id = 2002, UserName = "kid.two" };
        Assert.True((await users.CreateAsync(student, "student-pass-1")).Succeeded);
        var stampBefore = await users.GetSecurityStampAsync(student);

        var result = await scope.ServiceProvider.GetRequiredService<PasswordChangeService>()
            .ChangeAsync(student, "student-pass-1", "another-pass-2");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == PasswordChangeService.StudentRefusedCode);
        Assert.True(await users.CheckPasswordAsync(student, "student-pass-1"));
        Assert.Equal(stampBefore, await users.GetSecurityStampAsync(student));
    }

    private static async Task<VlmsIdentityUser> CreateAdultAsync(UserManager<VlmsIdentityUser> users)
    {
        var user = new VlmsIdentityUser { Id = 2001, UserName = "adult@example.com", Email = "adult@example.com", EmailConfirmed = true };
        var created = await users.CreateAsync(user, Current);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }
}
