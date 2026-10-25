using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;
using Vlms.Tests.Infrastructure;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Exercises <see cref="LessonPageEndpoint"/> on an in-memory test server, as <see cref="LessonFileEndpointTests"/> does:
/// lesson pictures and the offline page behind the lesson-library policy (role plus MFA), Teacher's Information pictures
/// behind their own staff-only policy, and the offline page's content and no-script Content-Security-Policy.
/// </summary>
public sealed class LessonPageEndpointTests : IAsyncLifetime
{
    private const string RolesHeader = "X-Test-Roles";
    private const string MfaHeader = "X-Test-Mfa";

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-lesson-page-endpoint-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("lesson-pictures/R5-01/loaves.jpg", [0xFF, 0xD8, 0xFF]);
        await blobs.UploadAsync("teachers-information/HERALD/room.jpg", [0xFF, 0xD8, 0xFE]);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IBlobStorage>(blobs);
        builder.Services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddScoped<ICurrentUserContext>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
            var roles = ParseRoles(http.Request.Headers[RolesHeader]);
            return new FakeCurrentUserContext(5, hasMfaEnabled: http.Request.Headers[MfaHeader] != "off", roles);
        });
        builder.Services.AddScoped<LessonLibraryService>();
        builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        builder.Services.AddAuthorization(o =>
        {
            o.AddLessonLibraryPolicy();
            o.AddTeachersInformationPolicy();
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapLessonFileEndpoint();
        _app.MapLessonPageEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 5, Code = "HERALD", Name = "Herald", TeachersInformation = "## For teachers only" });
        context.Lessons.Add(new Lesson
        {
            Id = 1, RankId = 1, Code = "R5-01", Title = "Prosphora & bread", Order = 1, IsActive = true,
            Body = "## Questions\n\n![Loaves](loaves.jpg)\n\n<script>alert(1)</script>", ContentVersion = "v1"
        });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "R5-02", Title = "Unconverted", Order = 2, IsActive = true });
        context.LessonPictures.Add(new LessonPicture { Id = 7, LessonId = 1, Order = 1, FileName = "loaves.jpg", BlobKey = "lesson-pictures/R5-01/loaves.jpg" });
        context.TeachersInformationPictures.Add(new TeachersInformationPicture { Id = 8, RankId = 1, Order = 1, FileName = "room.jpg", BlobKey = "teachers-information/HERALD/room.jpg" });
        context.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _anchor.Dispose();
    }

    private static Role[] ParseRoles(string? header) =>
        string.IsNullOrEmpty(header)
            ? []
            : header.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<Role>).ToArray();

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(RolesHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity("Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? roles, bool mfa = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (roles is not null)
        {
            request.Headers.Add(RolesHeader, roles);
        }
        if (!mfa)
        {
            request.Headers.Add(MfaHeader, "off");
        }
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("Teacher")]
    [InlineData("Approver")]
    [InlineData("Admin")]
    [InlineData("Student")]
    [InlineData("Parent")]
    [InlineData("NationalAdministrator")]
    public async Task Picture_LibraryRole_GetsItInline(string role)
    {
        var response = await GetAsync("/lessons/pictures/7", role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal([0xFF, 0xD8, 0xFF], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Picture_Anonymous_IsChallenged_SafeguardingOfficer_Forbidden_NoMfa_Forbidden_Unknown_NotFound()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/lessons/pictures/7", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/pictures/7", "SafeguardingOfficer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/pictures/7", "Teacher", mfa: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/lessons/pictures/999", "Teacher")).StatusCode);
    }

    [Theory]
    [InlineData("Teacher")]
    [InlineData("Admin")]
    [InlineData("NationalAdministrator")]
    public async Task TeachersInformationPicture_Staff_GetIt(string role)
    {
        var response = await GetAsync("/lessons/teachers-information/pictures/8", role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0xFF, 0xD8, 0xFE], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("Student")]
    [InlineData("Parent")]
    [InlineData("Approver")]
    [InlineData("SafeguardingOfficer")]
    public async Task TeachersInformationPicture_EveryoneElse_IsForbidden(string role) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/teachers-information/pictures/8", role)).StatusCode);

    [Fact]
    public async Task TeachersInformationPicture_StaffWithoutMfa_IsForbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/teachers-information/pictures/8", "Teacher", mfa: false)).StatusCode);

    [Theory]
    [InlineData("Student")]
    [InlineData("Teacher")]
    public async Task OfflinePage_IsOneSelfContainedPage_WithNoScriptAllowed_AndNoTeachersInformation(string role)
    {
        var response = await GetAsync("/lessons/R5-01/offline", role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.DoesNotContain("script-src", csp);
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Prosphora &amp; bread</title>", html);
        Assert.Contains("<h1>Prosphora &amp; bread</h1>", html);
        Assert.Contains("src=\"/lessons/pictures/7\"", html);
        Assert.Contains("<style>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("For teachers only", html);
    }

    [Fact]
    public async Task OfflinePage_UnconvertedOrUnknownLesson_IsNotFound_AndOutsiderIsRefused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/lessons/R5-02/offline", "Student")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/lessons/NOPE/offline", "Student")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/lessons/R5-01/offline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/R5-01/offline", "SafeguardingOfficer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/lessons/R5-01/offline", "Teacher", mfa: false)).StatusCode);
    }

    [Fact]
    public async Task LessonFileEndpoint_StillServesFilesOnly_NotPicturesById()
    {
        // Picture ids are their own sequence: /lessons/files/7 is not the picture with id 7.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/lessons/files/7", "Student")).StatusCode);
    }
}
