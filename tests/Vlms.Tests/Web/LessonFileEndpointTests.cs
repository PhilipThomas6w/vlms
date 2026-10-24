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
/// Exercises <see cref="LessonFileEndpoint"/> on an in-memory test server (same approach as
/// <see cref="WebHardeningTests"/>): the real policy, real <see cref="AnyRoleAuthorizationHandler"/>/
/// <see cref="MfaRequirementHandler"/>, the real <see cref="LessonLibraryService"/> over SQLite
/// in-memory, and <see cref="InMemoryBlobStorage"/>. Sign-in is faked by a header-driven
/// authentication scheme (<c>X-Test-Roles</c>, <c>X-Test-Mfa</c>); absent header means anonymous.
/// </summary>
public sealed class LessonFileEndpointTests : IAsyncLifetime
{
    private const string RolesHeader = "X-Test-Roles";
    private const string MfaHeader = "X-Test-Mfa";

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-endpoint-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("lessons/1/lesson.docx", [1, 2, 3]);
        await blobs.UploadAsync("lessons/1/handout.pdf", [37, 80, 68, 70]);
        await blobs.UploadAsync("lessons/1/diagram.PNG", [9]);
        await blobs.UploadAsync("lessons/1/picture.svg", [8]);
        await blobs.UploadAsync("lessons/2/old.pdf", [7]);

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
        builder.Services.AddAuthorization(o => o.AddLessonLibraryPolicy());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapLessonFileEndpoint();
        _app.MapSavedLessonsEndpoint();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var scope = _app.Services.CreateScope();
        var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "First" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Active", Order = 1, IsActive = true });
        context.Lessons.Add(new Lesson { Id = 2, RankId = 1, Code = "L2", Title = "Retired", Order = 2, IsActive = false });
        context.LessonFiles.Add(new LessonFile { Id = 1, LessonId = 1, Order = 1, FileName = "Lesson plan.docx", BlobKey = "lessons/1/lesson.docx" });
        context.LessonFiles.Add(new LessonFile { Id = 2, LessonId = 1, Order = 2, FileName = "Handout.pdf", BlobKey = "lessons/1/handout.pdf" });
        context.LessonFiles.Add(new LessonFile { Id = 3, LessonId = 1, Order = 3, FileName = "Diagram.PNG", BlobKey = "lessons/1/diagram.PNG" });
        context.LessonFiles.Add(new LessonFile { Id = 4, LessonId = 1, Order = 4, FileName = "Picture.svg", BlobKey = "lessons/1/picture.svg" });
        context.LessonFiles.Add(new LessonFile { Id = 5, LessonId = 2, Order = 1, FileName = "Old.pdf", BlobKey = "lessons/2/old.pdf" });
        context.SaveChanges();
        context.Dispose();
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
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private Task<HttpResponseMessage> GetAsync(int id, string? roles, bool mfa = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/lessons/files/{id}");
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
    public async Task AllowedRole_GetsTheFile(string role)
    {
        var response = await GetAsync(1, role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
    }

    private Task<HttpResponseMessage> GetSavedAsync(string query, string? roles, bool mfa = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/lessons/saved" + query);
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
    public async Task Saved_AllowedRole_GetsTheLessonWithItsFiles(string role)
    {
        var response = await GetSavedAsync("?id=1", role);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var lesson = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(1, lesson.GetProperty("id").GetInt32());
        Assert.Equal("Active", lesson.GetProperty("title").GetString());
        Assert.Equal("R1: First", lesson.GetProperty("rank").GetString());
        Assert.Equal([1, 2, 3, 4], lesson.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Saved_RetiredAndUnknownLessons_AreAbsent()
    {
        var response = await GetSavedAsync("?id=2&id=9999&id=1", "Teacher");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([1], json.RootElement.EnumerateArray().Select(l => l.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Saved_NoIds_IsAnEmptyList()
    {
        var response = await GetSavedAsync("", "Teacher");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saved_Anonymous_IsRefused()
    {
        var response = await GetSavedAsync("?id=1", roles: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Saved_SafeguardingOfficerOnly_IsForbidden()
    {
        var response = await GetSavedAsync("?id=1", "SafeguardingOfficer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Saved_TeacherWithoutMfa_IsForbidden()
    {
        var response = await GetSavedAsync("?id=1", "Teacher", mfa: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await GetAsync(1, roles: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SafeguardingOfficerOnly_IsForbidden()
    {
        var response = await GetAsync(1, "SafeguardingOfficer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminWithoutMfa_IsForbidden()
    {
        var response = await GetAsync(1, "Admin", mfa: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TeacherWithoutMfa_IsForbidden()
    {
        // Teacher is MFA-gated since 22/10/2026 (adr/0006 amendment).
        var response = await GetAsync(1, "Teacher", mfa: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ParentWithoutMfa_GetsTheFile()
    {
        var response = await GetAsync(1, "Parent", mfa: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FileOfInactiveLesson_IsNotFound()
    {
        var response = await GetAsync(5, "Admin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownId_IsNotFound()
    {
        var response = await GetAsync(9999, "Teacher");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BlobKeyInTheUrl_IsNotAnAddressableRoute()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/lessons/files/lessons%2F1%2Flesson.docx");
        request.Headers.Add(RolesHeader, "Admin");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_IsServedInlineWithItsContentType()
    {
        var response = await GetAsync(2, "Student");

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("inline", Assert.Single(response.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Image_IsServedInline_WhateverTheExtensionCase()
    {
        var response = await GetAsync(3, "Parent");

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("inline", Assert.Single(response.Content.Headers.GetValues("Content-Disposition")));
    }

    [Fact]
    public async Task Docx_IsAnAttachment_WithItsFileName()
    {
        var response = await GetAsync(1, "Teacher");

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            response.Content.Headers.ContentType?.MediaType);
        var disposition = Assert.Single(response.Content.Headers.GetValues("Content-Disposition"));
        Assert.StartsWith("attachment", disposition);
        Assert.Contains("Lesson plan.docx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Svg_IsNeverServedInline()
    {
        var response = await GetAsync(4, "Teacher");

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment", Assert.Single(response.Content.Headers.GetValues("Content-Disposition")));
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf", true)]
    [InlineData("a.PDF", "application/pdf", true)]
    [InlineData("a.png", "image/png", true)]
    [InlineData("a.jpg", "image/jpeg", true)]
    [InlineData("a.jpeg", "image/jpeg", true)]
    [InlineData("a.gif", "image/gif", true)]
    [InlineData("a.webp", "image/webp", true)]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("a.html", "text/html", false)]
    [InlineData("a.txt", "text/plain", false)]
    [InlineData("a.xyz", "application/octet-stream", false)]
    [InlineData("noextension", "application/octet-stream", false)]
    public void FileTypes_MapExtensionToContentTypeAndDisposition(string fileName, string contentType, bool inline)
    {
        Assert.Equal(contentType, LessonFileTypes.ContentTypeFor(fileName));
        Assert.Equal(inline, LessonFileTypes.IsInline(fileName));
    }
}
