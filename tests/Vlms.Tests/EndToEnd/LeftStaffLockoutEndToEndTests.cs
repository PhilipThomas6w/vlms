using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of FR-017 (Philip 22/10/2026, clarified 23/10/2026; TC-038): a Teacher,
/// Admin or Safeguarding Officer marked as left loses all staff access at once, and with no other
/// role is locked out entirely — the right password is refused, a sign-in half-way through two-factor
/// can't be finished, an open session loses its access at once and is ended at its next
/// security-stamp validation, and a password reset doesn't let them back in. One who is also a
/// parent signs in as a parent only. Students, parents and Approvers who leave keep access.
/// Leaving runs through the real <c>LeavingService.StepDownAsync</c>
/// (<see cref="VlmsWebFactory.StepDownAsync"/>).
/// </summary>
public sealed class LeftStaffLockoutEndToEndTests : IDisposable
{
    private const string GenericFailure = "Invalid sign-in details.";

    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(Role.Teacher, true)]
    [InlineData(Role.Teacher, false)]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.SafeguardingOfficer, true)]
    public async Task Left_staff_member_cannot_sign_in_with_the_right_password_and_sees_only_the_generic_message(Role role, bool withTotp)
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp, role);
        await _factory.StepDownAsync(boss.Id, leaver.Id);

        var client = _factory.CreateBrowser();
        var response = await Browser.PostLoginAsync(client, leaver.Email, VlmsWebFactory.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(GenericFailure, html);
        Assert.DoesNotContain("Confirm your email", html);
        Assert.DoesNotContain("locked", html);
        // Not signed in: an authorized page still sends them to log in.
        var page = await client.GetAsync("/Account/Manage/TwoFactorAuthentication");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(page), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_for_a_left_staff_member_gives_the_same_message_as_the_right_one()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        await _factory.StepDownAsync(boss.Id, leaver.Id);

        var wrong = await Browser.PostLoginAsync(_factory.CreateBrowser(), leaver.Email, "Not-The-Password-1!");

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains(GenericFailure, await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Staff_member_who_is_also_a_parent_signs_in_as_a_parent_only_after_leaving()
    {
        // FR-017 clarified 23/10/2026: they keep parent access to their own children; every staff
        // page refuses them.
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("staff-parent@test.example", withTotp: true,
            Role.Teacher, Role.Admin, Role.SafeguardingOfficer, Role.Parent);
        var before = await Browser.SignInAsync(_factory, leaver);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/students")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/administration/students")).StatusCode);

        await _factory.StepDownAsync(boss.Id, leaver.Id);
        var client = await Browser.SignInAsync(_factory, leaver);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/parent/dashboard")).StatusCode);
        foreach (var path in new[] { "/students", "/progress/record", "/lessons/new/RECRUIT", "/administration/students",
                     "/administration/user-roles", "/retention/mark-as-left", "/safeguarding/consent", "/safeguarding/dbs" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path} gave {(int)response.StatusCode}");
            Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Forgot_password_still_sends_a_link_to_a_left_staff_member_who_is_also_a_parent()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("staff-parent@test.example", withTotp: true, Role.Teacher, Role.Parent);
        await _factory.StepDownAsync(boss.Id, leaver.Id);

        await PostForgotPasswordAsync(leaver.Email);

        Assert.Contains(_factory.Email.SentEmails, e => e.RecipientEmail == leaver.Email && e.Subject.Contains("Reset"));
    }

    [Fact]
    public async Task Sign_in_half_way_through_two_factor_cannot_be_finished_with_the_authenticator_code_after_leaving()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, leaver.Email, VlmsWebFactory.Password);
        var twoFactorUrl = Browser.RelativeLocation(login);
        Assert.StartsWith("/Account/LoginWith2fa", twoFactorUrl, StringComparison.OrdinalIgnoreCase);

        await _factory.StepDownAsync(boss.Id, leaver.Id);
        var second = await Browser.PostTwoFactorAsync(client, twoFactorUrl, Browser.Totp(leaver.AuthenticatorKey!));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/students")).StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(await client.GetAsync("/students")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_half_way_through_two_factor_cannot_be_finished_with_a_recovery_code_after_leaving()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        string recoveryCode;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
            var user = await users.FindByIdAsync(leaver.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 1))!.Single();
        }

        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, leaver.Email, VlmsWebFactory.Password);
        Assert.StartsWith("/Account/LoginWith2fa", Browser.RelativeLocation(login), StringComparison.OrdinalIgnoreCase);

        await _factory.StepDownAsync(boss.Id, leaver.Id);
        const string url = "/Account/LoginWithRecoveryCode";
        var token = await Browser.GetAntiforgeryTokenAsync(client, url);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "login-with-recovery-code"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.RecoveryCode", recoveryCode),
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync());
        var page = await client.GetAsync("/students");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(page), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_session_loses_staff_pages_at_once_even_before_the_stamp_is_rechecked()
    {
        // Default 1-minute stamp interval: the session's cookie is still accepted, but the roles
        // already count for nothing (IdentityCurrentUserContext), so staff pages are refused.
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        var client = await Browser.SignInAsync(_factory, leaver);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/students")).StatusCode);

        await _factory.StepDownAsync(boss.Id, leaver.Id);

        var after = await client.GetAsync("/students");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task Open_session_is_signed_out_at_the_next_stamp_validation()
    {
        using var factory = new VlmsWebFactory { StampValidationInterval = TimeSpan.Zero };
        var boss = await factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Admin);
        var client = await Browser.SignInAsync(factory, leaver);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Manage/TwoFactorAuthentication")).StatusCode);

        await factory.StepDownAsync(boss.Id, leaver.Id);

        // Any signed-in page (not only staff ones) now sends them to log in: the session is over.
        var after = await client.GetAsync("/Account/Manage/TwoFactorAuthentication");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(after), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stamp_validation_refuses_a_left_staff_member_even_when_the_stamp_has_not_changed()
    {
        // A teacher marked as left before FR-017 (flag set, no stamp rotation, no sign-in lockout
        // flag): the stamp still matches, but VlmsSignInManager re-checks that the account may sign in.
        using var factory = new VlmsWebFactory { StampValidationInterval = TimeSpan.Zero };
        var leaver = await factory.SeedUserAsync("legacy@test.example", withTotp: true, Role.Teacher);
        var client = await Browser.SignInAsync(factory, leaver);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Manage/TwoFactorAuthentication")).StatusCode);

        await factory.WithDbAsync(async db =>
        {
            var user = await db.AppUsers.SingleAsync(u => u.Id == leaver.Id);
            db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            await db.SaveChangesAsync();
        });

        var after = await client.GetAsync("/Account/Manage/TwoFactorAuthentication");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(after), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forgot_password_sends_no_link_to_a_left_staff_member_and_says_the_same_thing()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        var colleague = await _factory.SeedUserAsync("colleague@test.example", withTotp: true, Role.Teacher);
        await _factory.StepDownAsync(boss.Id, leaver.Id);

        var leaverPage = await PostForgotPasswordAsync(leaver.Email);
        var colleaguePage = await PostForgotPasswordAsync(colleague.Email);

        Assert.DoesNotContain(_factory.Email.SentEmails, e => e.RecipientEmail == leaver.Email);
        // Positive control: the same form does send a link to a colleague who hasn't left.
        Assert.Contains(_factory.Email.SentEmails, e => e.RecipientEmail == colleague.Email && e.Subject.Contains("Reset"));
        Assert.Contains("If that email belongs to a confirmed account", leaverPage);
        Assert.Contains("If that email belongs to a confirmed account", colleaguePage);
    }

    [Fact]
    public async Task A_password_reset_does_not_let_a_left_staff_member_back_in()
    {
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var leaver = await _factory.SeedUserAsync("leaver@test.example", withTotp: true, Role.Teacher);
        await _factory.StepDownAsync(boss.Id, leaver.Id);

        // Suppose a reset happened anyway (e.g. a link they already held, or an operator's reset).
        const string newPassword = "Brand-New-Pass-7!";
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
            var user = await users.FindByIdAsync(leaver.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var code = await users.GeneratePasswordResetTokenAsync(user!);
            Assert.True((await users.ResetPasswordAsync(user!, code, newPassword)).Succeeded);
        }

        var response = await Browser.PostLoginAsync(_factory.CreateBrowser(), leaver.Email, newPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(GenericFailure, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reinstated_staff_member_signs_in_with_their_authenticator_and_has_staff_access_again()
    {
        // FR-017 amended 27/10/2026: reinstatement restores their roles; MFA still works.
        var boss = await _factory.SeedUserAsync("boss@test.example", withTotp: true, Role.Admin);
        var returner = await _factory.SeedUserAsync("returner@test.example", withTotp: true, Role.Teacher);
        await _factory.StepDownAsync(boss.Id, returner.Id);
        var refused = await Browser.PostLoginAsync(_factory.CreateBrowser(), returner.Email, VlmsWebFactory.Password);
        Assert.Contains(GenericFailure, await refused.Content.ReadAsStringAsync());

        await _factory.ReinstateAsync(boss.Id, returner.Id);
        var client = await Browser.SignInAsync(_factory, returner);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/students")).StatusCode);
    }
    [Fact]
    public async Task Parent_who_has_left_can_still_sign_in()
    {
        var parent = await _factory.SeedUserAsync("left-parent@test.example", withTotp: false, Role.Parent);
        await MarkLeftDirectlyAsync(parent.Id);

        var response = await Browser.PostLoginAsync(_factory.CreateBrowser(), parent.Email, VlmsWebFactory.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", Browser.RelativeLocation(response));
    }

    [Fact]
    public async Task Approver_only_account_that_has_left_can_still_sign_in()
    {
        var approver = await _factory.SeedUserAsync("left-approver@test.example", withTotp: false, Role.Approver);
        await MarkLeftDirectlyAsync(approver.Id);

        var response = await Browser.PostLoginAsync(_factory.CreateBrowser(), approver.Email, VlmsWebFactory.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", Browser.RelativeLocation(response));
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

    private Task MarkLeftDirectlyAsync(int userId) =>
        // No service marks a parent or Approver as left; set the flag as a leaver record would.
        _factory.WithDbAsync(async db =>
        {
            var user = await db.AppUsers.SingleAsync(u => u.Id == userId);
            db.Entry(user).CurrentValues[nameof(AppUser.HasLeft)] = true;
            db.Entry(user).CurrentValues[nameof(AppUser.LeftAt)] = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

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
