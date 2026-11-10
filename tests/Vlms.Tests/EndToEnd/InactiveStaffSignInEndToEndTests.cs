using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of FR-017 as amended 27/10/2026 (Inactive per chapter) and 03/11/2026:
/// a Teacher, Admin or Safeguarding Officer marked Inactive in a chapter keeps their account and can
/// still sign in (password, two-factor, recovery code, password reset), but every role they hold in
/// that chapter, Parent included, stops counting at once, open sessions included; the home page
/// says which chapter they are Inactive in. Reactivation restores the roles without a new invitation.
/// A student who has left keeps access. Inactivity runs through the real
/// <c>LeavingService.MarkInactiveAsync</c> (<see cref="VlmsWebFactory.MarkInactiveAsync"/>).
/// </summary>
public sealed class InactiveStaffSignInEndToEndTests : IDisposable
{
    private const string GenericFailure = "Invalid sign-in details.";

    private static readonly string[] ChapterPages =
    [
        "/students", "/progress/record", "/lessons/new/RECRUIT", "/administration/students",
        "/administration/user-roles", "/retention/mark-as-left", "/safeguarding/consent", "/safeguarding/dbs",
        "/parent/dashboard",
    ];

    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(Role.Teacher, true)]
    [InlineData(Role.Teacher, false)]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.SafeguardingOfficer, true)]
    public async Task Inactive_staff_member_can_still_sign_in_with_the_right_password(Role role, bool withTotp)
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp, role);
        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);

        var response = await Browser.PostLoginAsync(_factory.CreateBrowser(), inactive.Email, VlmsWebFactory.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(withTotp ? "/Account/LoginWith2fa" : "/", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_for_an_inactive_staff_member_gives_the_generic_message()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Teacher);
        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);

        var wrong = await Browser.PostLoginAsync(_factory.CreateBrowser(), inactive.Email, "Not-The-Password-1!");

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains(GenericFailure, await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Inactive_staff_parent_signs_in_sees_the_notice_and_is_refused_every_page_of_that_chapter()
    {
        // FR-017: Inactive suspends every role held in that chapter, Parent included.
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("staff-parent@test.example", withTotp: true,
            Role.Teacher, Role.Admin, Role.SafeguardingOfficer, Role.Parent);
        var before = await Browser.SignInAsync(_factory, inactive);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/students")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/parent/dashboard")).StatusCode);

        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);
        var client = await Browser.SignInAsync(_factory, inactive);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Your account is Inactive in", await home.Content.ReadAsStringAsync());
        foreach (var path in ChapterPages)
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path} gave {(int)response.StatusCode}");
            Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Forgot_password_sends_a_link_to_an_inactive_staff_member()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Teacher);
        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);

        var page = await PostForgotPasswordAsync(inactive.Email);

        Assert.Contains(_factory.Email.SentEmails, e => e.RecipientEmail == inactive.Email && e.Subject.Contains("Reset"));
        Assert.Contains("If that email belongs to a confirmed account", page);
    }

    [Fact]
    public async Task Sign_in_half_way_through_two_factor_can_be_finished_after_inactivity_but_gives_no_chapter_access()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Teacher);
        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, inactive.Email, VlmsWebFactory.Password);
        var twoFactorUrl = Browser.RelativeLocation(login);
        Assert.StartsWith("/Account/LoginWith2fa", twoFactorUrl, StringComparison.OrdinalIgnoreCase);

        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);
        var second = await Browser.PostTwoFactorAsync(client, twoFactorUrl, Browser.Totp(inactive.AuthenticatorKey!));

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        var students = await client.GetAsync("/students");
        Assert.Equal(HttpStatusCode.Redirect, students.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(students), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_with_a_recovery_code_works_after_inactivity_but_gives_no_chapter_access()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Teacher);
        string recoveryCode;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
            var user = await users.FindByIdAsync(inactive.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 1))!.Single();
        }

        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);
        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, inactive.Email, VlmsWebFactory.Password);
        Assert.StartsWith("/Account/LoginWith2fa", Browser.RelativeLocation(login), StringComparison.OrdinalIgnoreCase);
        const string url = "/Account/LoginWithRecoveryCode";
        var token = await Browser.GetAntiforgeryTokenAsync(client, url);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "login-with-recovery-code"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.RecoveryCode", recoveryCode),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var students = await client.GetAsync("/students");
        Assert.Equal(HttpStatusCode.Redirect, students.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(students), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_session_loses_chapter_pages_at_once_even_before_the_stamp_is_rechecked()
    {
        // Default 1-minute stamp interval: the session's cookie is still accepted, but the roles
        // already count for nothing in that chapter (IdentityCurrentUserContext), so its pages are refused.
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await _factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Teacher);
        var client = await Browser.SignInAsync(_factory, inactive);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/students")).StatusCode);

        await _factory.MarkInactiveAsync(boss.Id, inactive.Id);

        var after = await client.GetAsync("/students");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task Open_session_is_signed_out_at_the_next_stamp_validation()
    {
        // Marking someone Inactive rotates their security stamp (FR-016), so existing sessions end
        // and they sign in again under their new access.
        using var factory = new VlmsWebFactory { StampValidationInterval = TimeSpan.Zero };
        var boss = await factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var inactive = await factory.SeedUserAsync("inactive@test.example", withTotp: true, Role.Admin);
        var client = await Browser.SignInAsync(factory, inactive);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Manage/TwoFactorAuthentication")).StatusCode);

        await factory.MarkInactiveAsync(boss.Id, inactive.Id);

        var after = await client.GetAsync("/Account/Manage/TwoFactorAuthentication");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(after), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reactivated_staff_member_signs_in_with_their_authenticator_and_has_staff_access_again()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var returner = await _factory.SeedUserAsync("returner@test.example", withTotp: true, Role.Teacher);
        await _factory.MarkInactiveAsync(boss.Id, returner.Id);
        var whileInactive = await Browser.SignInAsync(_factory, returner);
        Assert.Equal(HttpStatusCode.Redirect, (await whileInactive.GetAsync("/students")).StatusCode);

        await _factory.ReactivateAsync(boss.Id, returner.Id);
        var client = await Browser.SignInAsync(_factory, returner);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/students")).StatusCode);
    }

    [Fact]
    public async Task Student_who_has_left_can_still_sign_in()
    {
        const string username = "kid.left";
        const string studentPassword = "student-pass-1";
        int userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
            var user = new VlmsIdentityUser { Id = 5001, UserName = username };
            Assert.True((await users.CreateAsync(user, studentPassword)).Succeeded);
            userId = user.Id;
        }

        await _factory.WithDbAsync(async db =>
        {
            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            db.AppUsers.Add(new AppUser { Id = userId, DisplayName = username, Email = "" });
            // Marked as left exactly as LeavingService.MarkStudentInactiveAsync leaves it.
            db.Students.Add(new Student
            {
                Id = 700, Name = "Kid Left", DateOfBirth = new DateOnly(2016, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Inactive, EnrolmentDate = new DateOnly(2025, 1, 1), AppUserId = userId,
                LeftAt = DateTime.UtcNow,
            });
            db.UserRoles.Add(new UserRole { UserId = userId, Role = Role.Student });
            await db.SaveChangesAsync();
        });

        var response = await Browser.PostLoginAsync(_factory.CreateBrowser(), username, studentPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", Browser.RelativeLocation(response));
    }

    private async Task<string> PostForgotPasswordAsync(string email)
    {
        var client = _factory.CreateBrowser();
        const string url = "/Account/ForgotPassword";
        var token = await Browser.GetAntiforgeryTokenAsync(client, url);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "forgot-password"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.Email", email),
        ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
