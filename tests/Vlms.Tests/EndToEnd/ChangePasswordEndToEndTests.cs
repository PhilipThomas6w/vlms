using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of /Account/Manage/ChangePassword (FR-011, adr/0007 decision 1, TC-029):
/// an adult changes their own password while signed in; a student never does.
/// </summary>
public sealed class ChangePasswordEndToEndTests : IDisposable
{
    private const string Url = "/Account/Manage/ChangePassword";
    private const string NewPassword = "Brand-New-Pass-7!";
    private const string StudentPassword = "student-pass-1";

    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Unauthenticated_request_is_redirected_to_login()
    {
        var response = await _factory.CreateBrowser().GetAsync(Url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adult_changes_password_with_the_right_current_password_and_can_sign_in_with_the_new_one()
    {
        var parent = await _factory.SeedUserAsync("change1@test.example", withTotp: false, Role.Parent);
        var client = await Browser.SignInAsync(_factory, parent);

        var response = await PostChangeAsync(client, VlmsWebFactory.Password, NewPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your password has been changed.", await response.Content.ReadAsStringAsync());
        // The refreshed cookie keeps this session signed in.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url)).StatusCode);

        var fresh = _factory.CreateBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await Browser.PostLoginAsync(fresh, parent.Email, NewPassword)).StatusCode);
        var old = _factory.CreateBrowser();
        var oldLogin = await Browser.PostLoginAsync(old, parent.Email, VlmsWebFactory.Password);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_is_refused_and_the_password_is_unchanged()
    {
        var parent = await _factory.SeedUserAsync("change2@test.example", withTotp: false, Role.Parent);
        var client = await Browser.SignInAsync(_factory, parent);

        var response = await PostChangeAsync(client, "Not-The-Password-1!", NewPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("Your password has been changed.", html);
        Assert.True(await PasswordIsAsync(parent.Id, VlmsWebFactory.Password));
        Assert.False(await PasswordIsAsync(parent.Id, NewPassword));
    }

    [Fact]
    public async Task Weak_new_password_is_rejected_with_the_reason_and_the_password_is_unchanged()
    {
        var parent = await _factory.SeedUserAsync("change3@test.example", withTotp: false, Role.Parent);
        var client = await Browser.SignInAsync(_factory, parent);

        var response = await PostChangeAsync(client, VlmsWebFactory.Password, "weak", "weak");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Passwords must be at least 6 characters", html);
        Assert.DoesNotContain("Your password has been changed.", html);
        Assert.True(await PasswordIsAsync(parent.Id, VlmsWebFactory.Password));
    }

    [Fact]
    public async Task Mismatched_confirmation_is_refused_and_the_password_is_unchanged()
    {
        var parent = await _factory.SeedUserAsync("change4@test.example", withTotp: false, Role.Parent);
        var client = await Browser.SignInAsync(_factory, parent);

        var response = await PostChangeAsync(client, VlmsWebFactory.Password, NewPassword, NewPassword + "x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Your password has been changed.", await response.Content.ReadAsStringAsync());
        Assert.True(await PasswordIsAsync(parent.Id, VlmsWebFactory.Password));
    }

    [Fact]
    public async Task Student_is_told_to_ask_their_parent_and_sees_no_form()
    {
        var studentId = await SeedStudentSignInAsync("kid.one");
        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, "kid.one", StudentPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var page = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Ask your parent", html);
        Assert.DoesNotContain("oldPassword", html);

        // No form is rendered for a student, so there is nothing to post; the handler's own refusal
        // (a hand-built POST) is covered by PasswordChangeServiceTests and the page's _isStudent guard.
        Assert.True(await PasswordIsAsync(studentId, StudentPassword));
    }

    private async Task<HttpResponseMessage> PostChangeAsync(HttpClient client, string current, string next, string confirm)
    {
        var token = await Browser.GetAntiforgeryTokenAsync(client, Url);
        return await client.PostAsync(Url, Form(token, current, next, confirm));
    }

    private static FormUrlEncodedContent Form(string token, string current, string next, string confirm) => new(
    [
        KeyValuePair.Create("_handler", "change-password"),
        KeyValuePair.Create("__RequestVerificationToken", token),
        KeyValuePair.Create("Input.OldPassword", current),
        KeyValuePair.Create("Input.NewPassword", next),
        KeyValuePair.Create("Input.ConfirmPassword", confirm),
    ]);

    private async Task<bool> PasswordIsAsync(int userId, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = await users.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await users.CheckPasswordAsync(user!, password);
    }

    /// <summary>A student sign-in: Identity user with a username and no email, paired AppUser, Student row, Student role.</summary>
    private async Task<int> SeedStudentSignInAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var user = new VlmsIdentityUser { Id = 5001, UserName = username };
        var created = await users.CreateAsync(user, StudentPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        await _factory.WithDbAsync(async db =>
        {
            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            db.AppUsers.Add(new AppUser { Id = user.Id, DisplayName = username, Email = "" });
            db.Students.Add(new Student
            {
                Id = 700, Name = "Kid One", DateOfBirth = new DateOnly(2016, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 1, 1), AppUserId = user.Id,
            });
            db.UserRoles.Add(new UserRole { UserId = user.Id, Role = Role.Student });
            await db.SaveChangesAsync();
        });

        return user.Id;
    }
}
