using System.Net;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests of parent registration with two-stage approval (functional.md FR-004 as
/// amended 22/10/2026, adr/0008; TC-037), over the real pipeline: a signed-in adult with no role
/// applies for two children on /registration/apply; nothing but the applications exists; a
/// Safeguarding Officer (with TOTP) asks for more information on /registration/review; the parent
/// answers on /registration/my-applications; the Safeguarding Officer approves; an Admin (with TOTP)
/// approves on /registration/approve at a chosen rank, which creates the student, link, consent and
/// Parent role; the parent then reaches the parent dashboard. Also: the stage pages refuse the
/// wrong roles, and a Teacher can no longer reach direct registration.
/// </summary>
public sealed class ParentRegistrationEndToEndTests : IDisposable
{
    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Parent_applies_safeguarding_asks_for_more_information_then_both_stages_approve_and_the_parent_gets_access()
    {
        await _factory.WithDbAsync(async db =>
        {
            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            db.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Reader" });
            await db.SaveChangesAsync();
        });
        var parentUser = await _factory.SeedUserAsync("new.parent@test.example", withTotp: false);
        var safeguarding = await _factory.SeedUserAsync("so@test.example", withTotp: true, Role.SafeguardingOfficer);
        var admin = await _factory.SeedUserAsync("admin@test.example", withTotp: true, Role.Admin);

        // 1. The parent applies for two children.
        var parent = await Browser.SignInAsync(_factory, parentUser);
        var applyPage = await parent.GetStringAsync("/registration/apply");
        Assert.Contains("Your phone number", applyPage);
        var apply = await PostFormAsync(parent, "/registration/apply", "apply-registration",
        [
            ("Input.Phone", "07000 000001"),
            ("Input.PostalAddress", "1 Church Lane, Shrewsbury"),
            ("Input.Command", "submit"),
            ("Input.Children[0].Name", "Chris Child"),
            ("Input.Children[0].DateOfBirth", "2016-05-06"),
            ("Input.Children[0].Relationship", "Mother"),
            ("Input.Children[0].PhotoMediaConsent", "true"),
            ("Input.Children[0].TransportConsent", "true"),
            ("Input.Children[0].EmergencyContact", "Grandma 07000 111222"),
            ("Input.Children[0].SpecialEducationalNeeds", "Dyslexia"),
            ("Input.Children[1].Name", "Dana Child"),
            ("Input.Children[1].DateOfBirth", "2018-01-02"),
            ("Input.Children[1].Relationship", "Mother"),
            ("Input.Children[1].EmergencyContact", "Grandma 07000 111222"),
        ]);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Contains("applications have been sent for review", await apply.Content.ReadAsStringAsync());

        int chrisId = 0;
        await _factory.WithDbAsync(async db =>
        {
            var applications = await db.StudentApplications.OrderBy(a => a.Id).ToListAsync();
            Assert.Equal(["Chris Child", "Dana Child"], applications.Select(a => a.ChildName));
            Assert.All(applications, a => Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview, a.Status));
            Assert.True(applications[0].TransportConsent);
            Assert.False(applications[0].OffsiteTripConsent);
            chrisId = applications[0].Id;
            Assert.Empty(await db.Students.ToListAsync());
            Assert.Empty(await db.StudentGuardianLinks.ToListAsync());
            Assert.Empty(await db.UserRoles.Where(r => r.UserId == parentUser.Id).ToListAsync());
        });
        Assert.Contains(_factory.Email.SentEmails, m => m.RecipientEmail == "so@test.example");

        // The parent cannot reach either review stage.
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/registration/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await parent.GetAsync("/registration/approve")).StatusCode);

        // 2. The Safeguarding Officer reads the details and asks for more information.
        var so = await Browser.SignInAsync(_factory, safeguarding);
        var review = await so.GetStringAsync($"/registration/review?id={chrisId}");
        Assert.Contains("Dyslexia", review);
        var ask = await PostFormAsync(so, $"/registration/review?id={chrisId}", "safeguarding-review",
            [("Input.Message", "What reading support helps?"), ("Input.Action", "more-information")]);
        Assert.Contains("asked for more information", await ask.Content.ReadAsStringAsync());

        // 3. The parent sees the question, answers and resubmits.
        var mine = await parent.GetStringAsync("/registration/my-applications");
        Assert.Contains("What reading support helps?", mine);
        var resubmit = await PostFormAsync(parent, $"/registration/my-applications?edit={chrisId}", "resubmit-application",
        [
            ("Input.Response", "A reading ruler"),
            ("Input.Phone", "07000 000001"),
            ("Input.PostalAddress", "1 Church Lane, Shrewsbury"),
            ("Input.Name", "Chris Child"),
            ("Input.DateOfBirth", "2016-05-06"),
            ("Input.Relationship", "Mother"),
            ("Input.PhotoMediaConsent", "true"),
            ("Input.TransportConsent", "true"),
            ("Input.EmergencyContact", "Grandma 07000 111222"),
            ("Input.SpecialEducationalNeeds", "Dyslexia; reading ruler"),
        ]);
        Assert.Contains("resubmitted for review", await resubmit.Content.ReadAsStringAsync());

        // 4. The Safeguarding Officer approves stage 1.
        var pass = await PostFormAsync(so, $"/registration/review?id={chrisId}", "safeguarding-review",
            [("Input.Message", ""), ("Input.Action", "approve")]);
        Assert.Contains("waiting for an Admin", await pass.Content.ReadAsStringAsync());

        // 5. The Admin approves at the second rank.
        var adminBrowser = await Browser.SignInAsync(_factory, admin);
        var approvePage = await adminBrowser.GetStringAsync($"/registration/approve?id={chrisId}");
        Assert.Contains("Starting rank", approvePage);
        var approve = await PostFormAsync(adminBrowser, $"/registration/approve?id={chrisId}", "admin-approval",
            [("Input.StartingRankId", "2"), ("Input.ExistingStudentId", ""), ("Input.Action", "approve")]);
        Assert.Contains("Chris Child is registered", await approve.Content.ReadAsStringAsync());

        await _factory.WithDbAsync(async db =>
        {
            var student = await db.Students.SingleAsync();
            Assert.Equal("Chris Child", student.Name);
            Assert.Equal(2, student.CurrentRankId);
            var link = await db.StudentGuardianLinks.Include(l => l.ParentGuardian).SingleAsync();
            Assert.Equal(parentUser.Id, link.ParentGuardian!.AppUserId);
            Assert.Equal(admin.Id, link.CreatedByUserId);
            Assert.Equal(ConsentStatus.Approved, (await db.ConsentRecords.SingleAsync()).Status);
            // The copied sensitive details are checked in StudentApplicationServiceTests: reading them
            // here would run the audit interceptor outside a request scope.
            Assert.Equal([Role.Parent], await db.UserRoles.Where(r => r.UserId == parentUser.Id).Select(r => r.Role).ToListAsync());
            var chris = await db.StudentApplications.SingleAsync(a => a.Id == chrisId);
            Assert.Equal(safeguarding.Id, chris.SafeguardingReviewedByUserId);
            Assert.Equal(admin.Id, chris.AdminDecidedByUserId);
            // The other child is still only an application.
            Assert.Equal(StudentApplicationStatus.AwaitingSafeguardingReview,
                (await db.StudentApplications.SingleAsync(a => a.ChildName == "Dana Child")).Status);
        });

        // 6. Signing in again (the role grant rotated the security stamp), the parent reaches the dashboard.
        var parentAgain = await Browser.SignInAsync(_factory, parentUser);
        var dashboard = await parentAgain.GetAsync("/parent/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Chris Child", await dashboard.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_duplicate_child_name_from_the_same_parent_is_refused_on_the_page()
    {
        var parentUser = await _factory.SeedUserAsync("dup.parent@test.example", withTotp: false);
        var parent = await Browser.SignInAsync(_factory, parentUser);

        var response = await PostFormAsync(parent, "/registration/apply", "apply-registration",
        [
            ("Input.Phone", "07000 000001"),
            ("Input.PostalAddress", "1 Church Lane"),
            ("Input.Command", "submit"),
            ("Input.Children[0].Name", "Chris Child"),
            ("Input.Children[0].DateOfBirth", "2016-05-06"),
            ("Input.Children[0].Relationship", "Father"),
            ("Input.Children[0].EmergencyContact", "Gran"),
            ("Input.Children[1].Name", "chris  child"),
            ("Input.Children[1].DateOfBirth", "2017-05-06"),
            ("Input.Children[1].Relationship", "Father"),
            ("Input.Children[1].EmergencyContact", "Gran"),
        ]);

        Assert.Contains("Each child can only be registered once", await response.Content.ReadAsStringAsync());
        await _factory.WithDbAsync(async db => Assert.Empty(await db.StudentApplications.ToListAsync()));
    }

    [Fact]
    public async Task A_teacher_can_no_longer_register_students_or_link_guardians()
    {
        var teacher = await _factory.SeedUserAsync("teacher@test.example", withTotp: true, Role.Teacher);
        var browser = await Browser.SignInAsync(_factory, teacher);

        foreach (var path in new[] { "/registration/students", "/guardianship/links", "/registration/review", "/registration/approve" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string handler, IEnumerable<(string Name, string Value)> fields)
    {
        var token = await Browser.GetAntiforgeryTokenAsync(client, url);
        var content = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("_handler", handler),
            KeyValuePair.Create("__RequestVerificationToken", token),
        };
        content.AddRange(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));
        var response = await client.PostAsync(url, new FormUrlEncodedContent(content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }
}
