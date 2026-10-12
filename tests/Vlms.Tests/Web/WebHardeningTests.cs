using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Exercises <see cref="WebHardening"/>'s real registrations on an in-memory test server: the
/// account-forms rate limit (POSTs only, per page), the HSTS lifetime, and the Production
/// allowed-hosts check. Plus source checks that every account form page opts in to the policy
/// and that Program.cs places the middleware after forwarded-headers handling.
/// </summary>
public sealed class WebHardeningTests
{
    private const string FormPath = "/account/form";

    private static async Task<WebApplication> StartAsync(
        string environment = "Development", Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        builder.Services.AddVlmsWebHardening(builder.Configuration, builder.Environment);

        var app = builder.Build();
        app.UseHsts();
        app.UseRateLimiter();
        app.MapMethods(FormPath, ["GET", "POST"], () => "ok").RequireRateLimiting(WebHardening.AccountFormsPolicy);
        app.MapMethods("/account/other-form", ["POST"], () => "ok").RequireRateLimiting(WebHardening.AccountFormsPolicy);
        await app.StartAsync();
        return app;
    }

    private static Dictionary<string, string?> Limit(int perMinute) =>
        new() { [WebHardening.AccountFormPostsPerMinuteKey] = perMinute.ToString() };

    [Fact]
    public async Task AccountForms_PostsBeyondTheLimit_Get429WithAPlainMessage()
    {
        await using var app = await StartAsync(settings: Limit(3));
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(FormPath, null)).StatusCode);
        }

        var rejected = await client.PostAsync(FormPath, null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(WebHardening.RejectionMessage, await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AccountForms_PageLoads_AreNotLimited()
    {
        await using var app = await StartAsync(settings: Limit(2));
        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(FormPath)).StatusCode);
        }
    }

    [Fact]
    public async Task AccountForms_EachPageHasItsOwnAllowance_AndPathCaseDoesNotEvadeIt()
    {
        await using var app = await StartAsync(settings: Limit(1));
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(FormPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(FormPath.ToUpperInvariant(), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/account/other-form", null)).StatusCode);
    }

    [Fact]
    public async Task AccountForms_DefaultLimit_Is20PostsPerMinute()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        for (var i = 0; i < WebHardening.DefaultAccountFormPostsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(FormPath, null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(FormPath, null)).StatusCode);
    }

    [Fact]
    public async Task Hsts_DefaultsToOneYear_WithoutSubdomainsOrPreload()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://www.varangians.co.uk/");

        var response = await client.GetAsync(FormPath);

        Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("www.varangians.co.uk;*")]
    public async Task Production_WithoutExplicitAllowedHosts_FailsAtStartup(string? allowedHosts)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync("Production", new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts }));
    }

    [Fact]
    public async Task Production_WithExplicitAllowedHosts_Starts_AndDevelopmentMayUseWildcard()
    {
        await using var production = await StartAsync("Production", new Dictionary<string, string?> { ["AllowedHosts"] = "www.varangians.co.uk" });
        await using var development = await StartAsync("Development", new Dictionary<string, string?> { ["AllowedHosts"] = "*" });
    }

    [Theory]
    [InlineData("Components/Account/Pages/Login.razor")]
    [InlineData("Components/Account/Pages/LoginWith2fa.razor")]
    [InlineData("Components/Account/Pages/LoginWithRecoveryCode.razor")]
    [InlineData("Components/Account/Pages/Register.razor")]
    [InlineData("Components/Account/Pages/ForgotPassword.razor")]
    [InlineData("Components/Account/Pages/ResetPassword.razor")]
    [InlineData("Components/Account/Pages/ResendEmailConfirmation.razor")]
    [InlineData("Components/Pages/Guardianship/AcceptInvite.razor")]
    public void AccountFormPage_OptsInToTheRateLimit(string page)
    {
        Assert.Contains("@attribute [EnableRateLimiting(WebHardening.AccountFormsPolicy)]", Read("src", "Vlms.Web", page));
    }

    [Fact]
    public void Program_RegistersHardening_AndRateLimitsAfterForwardedHeaders()
    {
        var program = Read("src", "Vlms.Web", "Program.cs");

        Assert.Contains("builder.Services.AddVlmsWebHardening(builder.Configuration, builder.Environment);", program);
        var forwarded = program.IndexOf("app.UseForwardedHeaders(", StringComparison.Ordinal);
        var rateLimiter = program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        Assert.True(forwarded >= 0 && rateLimiter > forwarded, "UseRateLimiter must come after UseForwardedHeaders so limits use the real client IP");
    }

    [Fact]
    public void WebUnit_SetsTheRealAllowedHost()
    {
        Assert.Contains("Environment=AllowedHosts=www.varangians.co.uk", Read("deploy", "systemd", "vlms-web.service"));
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
