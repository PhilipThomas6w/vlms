using System.Net;
using System.Text.RegularExpressions;
using Vlms.Domain;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of adr/0017 over the real pipeline: no role, no access (a signed-in person
/// with no role sees on Home only where their registration stands, and the menu offers nothing but
/// Home); only a Parent registers a child; someone with an application made before that change keeps
/// My applications; the Admin's one Registrations page, counted in the menu and on Home.
/// </summary>
public sealed partial class RegistrationQueueEndToEndTests : IDisposable
{
    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("""<nav aria-label="Main">(.*?)</nav>""", RegexOptions.Singleline)]
    private static partial Regex MainNav();

    [GeneratedRegex("<a href=\"([^\"]+)\"")]
    private static partial Regex Href();

    private static List<string> NavLinks(string html) =>
        Href().Matches(MainNav().Match(html).Groups[1].Value).Select(m => m.Groups[1].Value).ToList();

    private async Task RegisterForAsync(int userId, JoiningAs joiningAs, bool dismissed = false) =>
        await _factory.WithDbAsync(async db =>
        {
            db.ChapterJoinRequests.Add(new ChapterJoinRequest
            {
                UserId = userId, ChapterId = Chapter.ShrewsburyId, JoiningAs = joiningAs, RequestedAt = DateTime.UtcNow,
                DismissedAt = dismissed ? DateTime.UtcNow : null
            });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Someone_with_no_role_sees_only_that_their_registration_is_in_progress_and_no_menu_but_Home()
    {
        var newcomer = await _factory.SeedUserAsync("newcomer@test.example", withTotp: false);
        await RegisterForAsync(newcomer.Id, JoiningAs.Parent);
        var browser = await Browser.SignInAsync(_factory, newcomer);

        var home = await browser.GetStringAsync("/");

        Assert.Contains("Your registration is in progress. An Admin of Shrewsbury Orthodox Church will give you access.", home);
        Assert.Equal(["/"], NavLinks(home));
        Assert.DoesNotContain("Register your child", home);
        Assert.DoesNotContain("What to do next", home);

        foreach (var path in new[] { "/registration/apply", "/registration/my-applications", "/calendar", "/lessons" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Someone_dismissed_with_no_role_is_told_their_registration_was_not_approved()
    {
        var dismissed = await _factory.SeedUserAsync("dismissed@test.example", withTotp: false);
        await RegisterForAsync(dismissed.Id, JoiningAs.Other, dismissed: true);
        var browser = await Browser.SignInAsync(_factory, dismissed);

        var home = await browser.GetStringAsync("/");

        Assert.Contains("Your registration was not approved. Please contact your chapter.", home);
        Assert.Equal(["/"], NavLinks(home));
    }

    [Fact]
    public async Task A_member_of_staff_who_is_not_a_Parent_cannot_register_a_child_and_is_not_offered_it()
    {
        var teacher = await _factory.SeedUserAsync("teacher@test.example", withTotp: true, Role.Teacher);
        var browser = await Browser.SignInAsync(_factory, teacher);

        var home = await browser.GetStringAsync("/");
        Assert.DoesNotContain("Register your child", home);
        Assert.DoesNotContain("/registration/apply", NavLinks(home));
        Assert.DoesNotContain("/registration/my-applications", NavLinks(home));

        var apply = await browser.GetAsync("/registration/apply");
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(apply), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Parent_is_offered_registering_a_child()
    {
        var parent = await _factory.SeedUserAsync("parent@test.example", withTotp: false, Role.Parent);
        var browser = await Browser.SignInAsync(_factory, parent);

        var home = await browser.GetStringAsync("/");
        Assert.Contains("Register your child", home);
        Assert.Contains("/registration/apply", NavLinks(home));
        Assert.Contains("/registration/my-applications", NavLinks(home));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/registration/apply")).StatusCode);
    }

    [Fact]
    public async Task Someone_with_an_application_from_before_but_no_role_keeps_My_applications_only()
    {
        var legacy = await _factory.SeedUserAsync("legacy@test.example", withTotp: false);
        await _factory.WithDbAsync(async db =>
        {
            db.StudentApplications.Add(new StudentApplication
            {
                Id = 1, ChapterId = Chapter.ShrewsburyId, ApplicantUserId = legacy.Id, ParentPhone = "07000 000000",
                ParentPostalAddress = "1 Church Lane", Relationship = "Mother", ChildName = "Lee Legacy", ChildDateOfBirth = new DateOnly(2016, 1, 1),
                PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
                Status = StudentApplicationStatus.AwaitingSafeguardingReview, SubmittedAt = DateTime.UtcNow, LastSubmittedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });
        var browser = await Browser.SignInAsync(_factory, legacy);

        var home = await browser.GetStringAsync("/");
        Assert.Equal(["/", "/registration/my-applications"], NavLinks(home));
        Assert.Contains("Lee Legacy", await browser.GetStringAsync("/registration/my-applications"));
        var apply = await browser.GetAsync("/registration/apply");
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
    }

    [Fact]
    public async Task The_Admin_sees_one_Registrations_page_counted_in_the_menu_and_on_Home()
    {
        var admin = await _factory.SeedUserAsync("admin@test.example", withTotp: true, Role.Admin);
        var teacherToBe = await _factory.SeedUserAsync("teacher.to.be@test.example", withTotp: false);
        var parentToBe = await _factory.SeedUserAsync("parent.to.be@test.example", withTotp: false);
        await RegisterForAsync(teacherToBe.Id, JoiningAs.Teacher);
        await RegisterForAsync(parentToBe.Id, JoiningAs.Parent);
        var browser = await Browser.SignInAsync(_factory, admin);

        var home = await browser.GetStringAsync("/");
        Assert.Contains("Registrations (2 waiting)", home);
        Assert.Contains("<span class=\"nav-count\" aria-hidden=\"true\">2</span>", home);
        Assert.DoesNotContain("Approve registration", home);

        var page = await browser.GetStringAsync("/registration/approve");
        Assert.Contains("<title>Registrations</title>", page);
        Assert.Contains("Waiting for a role", page);
        Assert.Contains("Children's applications", page);
        Assert.Contains("With the Safeguarding Officer", page);

        var roles = await browser.GetStringAsync("/administration/user-roles");
        Assert.DoesNotContain("New registrations", roles);
        Assert.DoesNotContain("Waiting for approval", roles);
        Assert.Contains("href=\"/registration/approve\"", roles);
    }
}
