using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure.Guardianship;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of /guardianship/accept-invite for an invite sent to an email that already
/// has an account (functional.md FR-004, 22/10/2026; TC-033): the page asks the invitee to sign in,
/// sign-in (including the TOTP step) returns to the invite, and accepting links the signed-in
/// account. A different signed-in account is refused. The token is minted through the app's own
/// GuardianInviteService key ring, as the emailed link would carry it.
/// </summary>
public sealed class GuardianInviteEndToEndTests : IDisposable
{
    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Teacher_with_MFA_signs_in_from_the_invite_and_accepting_links_their_account()
    {
        var teacher = await _factory.SeedUserAsync("dual.role@test.example", withTotp: true, Role.Teacher);
        var url = await SeedInviteAsync("Dual.Role@Test.Example");

        var browser = _factory.CreateBrowser();
        var prompt = await browser.GetStringAsync(url);
        Assert.Contains(GuardianInviteService.SignInToAcceptMessage, prompt);
        Assert.DoesNotContain("id=\"password\"", prompt);

        // Follow the page's own sign-in link: password, then TOTP, then back to the invite.
        var signInHref = WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Match(prompt, "href=\"(Account/Login\\?ReturnUrl=[^\"]+)\"").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(signInHref));
        var loginUrl = "/" + signInHref;
        var token = await Browser.GetAntiforgeryTokenAsync(browser, loginUrl);
        var login = await browser.PostAsync(loginUrl, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "login"),
            KeyValuePair.Create("__RequestVerificationToken", token),
            KeyValuePair.Create("Input.UserName", teacher.Email),
            KeyValuePair.Create("Input.Password", VlmsWebFactory.Password),
        ]));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var twoFactorUrl = Browser.RelativeLocation(login);
        Assert.StartsWith("/Account/LoginWith2fa", twoFactorUrl, StringComparison.OrdinalIgnoreCase);
        var second = await Browser.PostTwoFactorAsync(browser, twoFactorUrl, Browser.Totp(teacher.AuthenticatorKey!));
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(url, Browser.RelativeLocation(second));

        var ready = await browser.GetStringAsync(url);
        Assert.Contains("Accept invite", ready);

        var accept = await PostAcceptAsync(browser, url);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var acceptedPage = await accept.Content.ReadAsStringAsync();
        Assert.Contains("Invite accepted", acceptedPage);
        // No Parent role yet: the original wording.
        Assert.Contains("Once an Admin has approved you as a parent", acceptedPage);

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(teacher.Id, (await db.ParentGuardians.SingleAsync(g => g.Id == 900)).AppUserId);
            Assert.Equal(teacher.Id, (await db.GuardianInvites.SingleAsync(i => i.Id == 1)).ConsumedByAppUserId);
            // No role granted by accepting.
            Assert.Equal([Role.Teacher], await db.UserRoles.Where(r => r.UserId == teacher.Id).Select(r => r.Role).ToListAsync());
        });

        // Single use.
        var again = await browser.GetStringAsync(url);
        Assert.Contains("already been used", again);
    }

    [Fact]
    public async Task Accepting_as_someone_who_already_holds_Parent_says_the_child_now_appears_on_the_dashboard()
    {
        var parent = await _factory.SeedUserAsync("existing.parent@test.example", withTotp: false, Role.Parent);
        var url = await SeedInviteAsync("existing.parent@test.example");

        var browser = await Browser.SignInAsync(_factory, parent);
        var accept = await PostAcceptAsync(browser, url);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var page = await accept.Content.ReadAsStringAsync();
        Assert.Contains("Invite accepted", page);
        Assert.Contains("the child now appears on", page);
        Assert.Contains("href=\"parent/dashboard\"", page);
        Assert.DoesNotContain("Once an Admin has approved you as a parent", page);
    }

    [Fact]
    public async Task A_different_signed_in_account_is_refused_and_nothing_is_linked()
    {
        await _factory.SeedUserAsync("invited@test.example", withTotp: false, Role.Teacher);
        var other = await _factory.SeedUserAsync("other@test.example", withTotp: false, Role.Approver);
        var url = await SeedInviteAsync("invited@test.example");

        var browser = await Browser.SignInAsync(_factory, other);
        var page = await browser.GetStringAsync(url);
        Assert.Contains(WebUtility.HtmlEncode(GuardianInviteService.DifferentAccountMessage), page);
        Assert.DoesNotContain("invited@test.example", page);
        Assert.DoesNotContain("Accept invite</button>", page);

        await _factory.WithDbAsync(async db =>
            Assert.Null((await db.ParentGuardians.SingleAsync(g => g.Id == 900)).AppUserId));
    }

    private static async Task<HttpResponseMessage> PostAcceptAsync(HttpClient browser, string url)
    {
        var token = await Browser.GetAntiforgeryTokenAsync(browser, url);
        return await browser.PostAsync(url, new FormUrlEncodedContent(
        [
            KeyValuePair.Create("_handler", "accept-invite-signed-in"),
            KeyValuePair.Create("__RequestVerificationToken", token),
        ]));
    }

    /// <summary>Seeds a student, a guardian with <paramref name="contactEmail"/> and invite 1; returns the accept URL.</summary>
    private async Task<string> SeedInviteAsync(string contactEmail)
    {
        await _factory.WithDbAsync(async db =>
        {
            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            db.Students.Add(new Student
            {
                Id = 800, Name = "Child One", DateOfBirth = new DateOnly(2015, 1, 1), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 1, 1),
            });
            db.ParentGuardians.Add(new ParentGuardian { Id = 900, Name = "Dual Role", ContactInfo = contactEmail, IsPrimary = true });
            var creator = await db.AppUsers.Select(u => u.Id).FirstAsync();
            db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 800, ParentGuardianId = 900, CreatedByUserId = creator });
            var now = DateTimeOffset.UtcNow;
            db.GuardianInvites.Add(new GuardianInvite
            {
                Id = 1, StudentId = 800, ParentGuardianId = 900, CreatedByUserId = creator,
                CreatedAt = now, ExpiresAt = now.Add(GuardianInviteService.DefaultInviteLifetime),
            });
            await db.SaveChangesAsync();
        });

        // Same purpose string and protector shape as GuardianInviteService.IssueAsync, through the
        // app's own Data Protection key ring.
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Vlms.GuardianInvite.v1").ToTimeLimitedDataProtector();
        var token = protector.Protect("1", GuardianInviteService.DefaultInviteLifetime);
        return "/guardianship/accept-invite?token=" + Uri.EscapeDataString(token);
    }
}
