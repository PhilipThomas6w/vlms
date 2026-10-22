using System.Net;
using Vlms.Domain;
using Vlms.Infrastructure.Storage;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Request-level tests over the real Vlms.Web pipeline (see <see cref="VlmsWebFactory"/>): anonymous
/// access, the static-SSR sign-in forms, a Teacher without MFA, and the lesson-file endpoint.
/// Written to fail on both defects that once reached master with every test green: protected pages
/// answering 500 because the AuthenticationStateProvider was not primed before authorization, and
/// every sign-in answering 500 because IdentityRedirectManager threw after NavigateTo.
/// </summary>
public sealed class AccessEndToEndTests : IDisposable
{
    private readonly VlmsWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Anonymous_home_page_is_200()
    {
        var response = await _factory.CreateBrowser().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/lessons")]
    [InlineData("/students")]
    [InlineData("/administration/students")]
    [InlineData("/parent/dashboard")]
    [InlineData("/safeguarding/consent")]
    [InlineData("/lessons/files/1")]
    public async Task Anonymous_protected_pages_redirect_to_login_never_500(string path)
    {
        var response = await _factory.CreateBrowser().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Parent_without_mfa_signs_in_and_is_sent_home()
    {
        var parent = await _factory.SeedUserAsync("parent@test.example", withTotp: false, Role.Parent);
        var client = _factory.CreateBrowser();

        var response = await Browser.PostLoginAsync(client, parent.Email, VlmsWebFactory.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", Browser.RelativeLocation(response));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/parent/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Admin_with_totp_signs_in_through_the_two_factor_step()
    {
        var admin = await _factory.SeedUserAsync("admin@test.example", withTotp: true, Role.Admin);
        var client = _factory.CreateBrowser();

        var login = await Browser.PostLoginAsync(client, admin.Email, VlmsWebFactory.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var twoFactorUrl = Browser.RelativeLocation(login);
        Assert.StartsWith("/Account/LoginWith2fa", twoFactorUrl, StringComparison.OrdinalIgnoreCase);

        // Not signed in yet: password alone must not open an Admin page.
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/administration/students")).StatusCode);

        var second = await Browser.PostTwoFactorAsync(client, twoFactorUrl, Browser.Totp(admin.AuthenticatorKey!));
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal("/", Browser.RelativeLocation(second));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/administration/students")).StatusCode);
    }

    [Fact]
    public async Task Wrong_totp_code_stays_on_the_page_with_an_error_not_a_500()
    {
        var admin = await _factory.SeedUserAsync("admin2@test.example", withTotp: true, Role.Admin);
        var client = _factory.CreateBrowser();
        var login = await Browser.PostLoginAsync(client, admin.Email, VlmsWebFactory.Password);
        var twoFactorUrl = Browser.RelativeLocation(login);
        var good = Browser.Totp(admin.AuthenticatorKey!);
        var bad = good == "000000" ? "000001" : "000000";

        var response = await Browser.PostTwoFactorAsync(client, twoFactorUrl, bad);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/administration/students")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_stays_on_the_login_page_with_an_error_not_a_500()
    {
        var parent = await _factory.SeedUserAsync("parent2@test.example", withTotp: false, Role.Parent);
        var client = _factory.CreateBrowser();

        var response = await Browser.PostLoginAsync(client, parent.Email, "Not-The-Password-1!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid sign-in details.", html);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/parent/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Teacher_without_mfa_is_sent_to_access_denied_on_teacher_pages_and_can_reach_enrolment()
    {
        var teacher = await _factory.SeedUserAsync("teacher-nomfa@test.example", withTotp: false, Role.Teacher);
        var client = await Browser.SignInAsync(_factory, teacher);

        foreach (var path in new[] { "/students", "/progress/record", "/curriculum/teacher", "/lessons" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path} gave {(int)response.StatusCode}");
            Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(response), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/AccessDenied")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Manage/TwoFactorAuthentication")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Manage/EnableAuthenticator")).StatusCode);
    }

    [Fact]
    public async Task Lesson_file_endpoint_serves_a_seeded_file_to_a_teacher_and_404s_an_unknown_id()
    {
        var bytes = new byte[] { 37, 80, 68, 70, 45, 49 };
        await new LocalDiskBlobStorage(_factory.BlobRoot).UploadAsync("lessons/1/handout.pdf", bytes);
        await _factory.WithDbAsync(async db =>
        {
            db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "First" });
            db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Active", Order = 1, IsActive = true });
            db.LessonFiles.Add(new LessonFile { Id = 1, LessonId = 1, Order = 1, FileName = "Handout.pdf", BlobKey = "lessons/1/handout.pdf" });
            await db.SaveChangesAsync();
        });
        var teacher = await _factory.SeedUserAsync("teacher-file@test.example", withTotp: true, Role.Teacher);
        var client = await Browser.SignInAsync(_factory, teacher);

        var ok = await client.GetAsync("/lessons/files/1");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/pdf", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(ok.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(bytes, await ok.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/lessons/files/999")).StatusCode);
    }
}
