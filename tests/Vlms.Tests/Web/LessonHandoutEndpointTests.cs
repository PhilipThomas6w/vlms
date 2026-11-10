using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
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
/// The handout endpoints (docs/adr/0016) on an in-memory test server with the real policies, handlers, antiforgery and
/// services, as <see cref="LessonSuggestionEndpointTests"/>: the upload needs a valid antiforgery token (shown both ways),
/// the author and a real file of an allowed type; a suggestion's handout is for its author and Approvers, a version's for
/// Approvers and Admins; a lesson's handout is downloaded inline (PDF, pictures) or as an attachment (Word), with nosniff.
/// </summary>
public sealed class LessonHandoutEndpointTests : IAsyncLifetime
{
    private const string UserHeader = "X-Test-User";
    private const string RolesHeader = "X-Test-Roles";
    private const int Author = 10, OtherTeacher = 11, Approver = 20;

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-handout-endpoint-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("lesson-handouts/R1-01/aaaa.pdf", HandoutBytes.Pdf());
        await blobs.UploadAsync("lesson-handouts/R1-01/bbbb.docx", HandoutBytes.Docx());
        await blobs.UploadAsync("lesson-handouts/suggestions/1/cccc.png", HandoutBytes.Png());

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IBlobStorage>(blobs);
        builder.Services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddScoped<ICurrentUserContext>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
            var roles = string.IsNullOrEmpty(http.Request.Headers[RolesHeader])
                ? []
                : http.Request.Headers[RolesHeader].ToString().Split(',').Select(Enum.Parse<Role>).ToArray();
            return new FakeCurrentUserContext(int.TryParse(http.Request.Headers[UserHeader], out var id) ? id : null, roles);
        });
        builder.Services.AddScoped<LessonSuggestionService>();
        builder.Services.AddScoped<LessonLibraryService>();
        builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        builder.Services.AddAuthorization(o =>
        {
            o.AddLessonSuggestionPolicies();
            o.AddLessonLibraryPolicy();
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAntiforgery();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapLessonSuggestionEndpoints();
        _app.MapLessonFileEndpoint();
        // Test-only: hands out an antiforgery token pair, as a page would.
        _app.MapGet("/test/token", (IAntiforgery antiforgery, HttpContext http) => antiforgery.GetAndStoreTokens(http).RequestToken!);
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options, new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "RECRUIT", Name = "Recruit" });
        foreach (var id in new[] { Author, OtherTeacher, Approver })
        {
            context.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = $"user{id}@example.com" });
        }
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "R1-01", Title = "Lesson", Order = 1, IsActive = true, Body = "Text.\n" });
        context.LessonFiles.Add(new LessonFile { Id = 1, LessonId = 1, Order = 1, FileName = "Template.pdf", DisplayName = "Template", BlobKey = "lesson-handouts/R1-01/aaaa.pdf" });
        context.LessonFiles.Add(new LessonFile { Id = 2, LessonId = 1, Order = 2, FileName = "Worksheet.docx", DisplayName = "Worksheet", BlobKey = "lesson-handouts/R1-01/bbbb.docx" });
        context.LessonVersions.Add(new LessonVersion
        {
            Id = 1, LessonId = 1, Number = 1, Kind = LessonVersionKind.Converted, Title = "Lesson", Order = 1, IsActive = true,
            Body = "Text.\n", PublishedAt = DateTime.UtcNow
        });
        context.LessonVersionHandouts.Add(new LessonVersionHandout { Id = 1, LessonVersionId = 1, Order = 1, FileName = "Template.pdf", DisplayName = "Template", BlobKey = "lesson-handouts/R1-01/aaaa.pdf" });
        context.LessonSuggestions.Add(new LessonSuggestion
        {
            Id = 1, LessonId = 1, RankId = 1, Kind = LessonChangeKind.Edit, Status = LessonSuggestionStatus.Draft, BaseVersionNumber = 1,
            AuthorUserId = Author, Title = "Lesson", Order = 1, Body = "Text.\n", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        context.LessonSuggestionHandouts.Add(new LessonSuggestionHandout { Id = 1, LessonSuggestionId = 1, Order = 1, FileName = "Colouring.png", DisplayName = "Colouring", BlobKey = "lesson-handouts/suggestions/1/cccc.png" });
        context.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _anchor.Dispose();
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.ContainsKey(RolesHeader)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Request.Headers[UserHeader].ToString())], "Test")), "Test"))
                : AuthenticateResult.NoResult());
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, int? user, string? roles, HttpContent? content = null,
        (string Cookie, string Token)? antiforgery = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (roles is not null)
        {
            request.Headers.Add(RolesHeader, roles);
            request.Headers.Add(UserHeader, user?.ToString() ?? "");
        }
        if (antiforgery is { } pair)
        {
            request.Headers.Add("Cookie", pair.Cookie);
            request.Headers.Add("RequestVerificationToken", pair.Token);
        }
        return _client.SendAsync(request);
    }

    private async Task<(string Cookie, string Token)> TokenAsync(int user, string roles)
    {
        var response = await SendAsync(HttpMethod.Get, "/test/token", user, roles);
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        return (cookie, await response.Content.ReadAsStringAsync());
    }

    private static MultipartFormDataContent Upload(byte[] content, string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content), "file", name);
        return form;
    }

    private int SuggestionHandoutCount()
    {
        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_anchor.ConnectionString).Options, new FakeCurrentUserContext(1, Role.Admin));
        return context.LessonSuggestionHandouts.Count();
    }

    [Fact]
    public async Task Upload_WithAValidAntiforgeryToken_ByTheAuthor_IsStored()
    {
        var token = await TokenAsync(Author, "Teacher");

        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/handouts", Author, "Teacher", Upload(HandoutBytes.Pdf("new"), "New sheet.pdf"), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"displayName\":\"New sheet\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, SuggestionHandoutCount());
    }

    [Fact]
    public async Task Upload_WithoutAnAntiforgeryToken_IsRefused()
    {
        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/handouts", Author, "Teacher", Upload(HandoutBytes.Pdf("new"), "New.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, SuggestionHandoutCount());
    }

    [Fact]
    public async Task Upload_OfAFileWhoseContentIsNotItsType_IsRefusedWithAMessage()
    {
        var token = await TokenAsync(Author, "Teacher");

        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/handouts", Author, "Teacher",
            Upload("<html><script>alert(1)</script></html>"u8.ToArray(), "sheet.pdf"), token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must be a PDF", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, SuggestionHandoutCount());
    }

    [Theory]
    [InlineData(OtherTeacher, "Teacher")]
    [InlineData(Approver, "Approver")]
    [InlineData(1, "Admin")]
    [InlineData(1, "Parent")]
    [InlineData(1, "Student")]
    public async Task Upload_ByAnyoneButTheAuthor_IsForbidden(int user, string roles)
    {
        var token = await TokenAsync(user, roles);

        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/handouts", user, roles, Upload(HandoutBytes.Pdf("new"), "New.pdf"), token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, SuggestionHandoutCount());
    }

    [Theory]
    [InlineData(Author, "Teacher", HttpStatusCode.OK)]
    [InlineData(Approver, "Approver", HttpStatusCode.OK)]
    [InlineData(OtherTeacher, "Teacher", HttpStatusCode.Forbidden)]
    [InlineData(1, "Admin", HttpStatusCode.Forbidden)]
    [InlineData(1, "Student", HttpStatusCode.Forbidden)]
    public async Task ASuggestionsHandout_IsForItsAuthorAndApprovers(int user, string roles, HttpStatusCode expected)
    {
        var response = await SendAsync(HttpMethod.Get, "/curriculum/suggestions/1/handouts/1", user, roles);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Approver", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Teacher", HttpStatusCode.Forbidden)]
    [InlineData("Student", HttpStatusCode.Forbidden)]
    [InlineData("Parent", HttpStatusCode.Forbidden)]
    [InlineData("SafeguardingOfficer", HttpStatusCode.Forbidden)]
    public async Task AVersionsHandout_IsForApproversAndAdmins(string roles, HttpStatusCode expected)
    {
        var response = await SendAsync(HttpMethod.Get, "/lessons/versions/handouts/1", 1, roles);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(1, "inline", "application/pdf")]
    [InlineData(2, "attachment", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public async Task ALessonsHandout_IsServedInlineOrAsAnAttachment_WithNosniff(int id, string disposition, string type)
    {
        var response = await SendAsync(HttpMethod.Get, $"/lessons/files/{id}", 1, "Student");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(disposition, response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
