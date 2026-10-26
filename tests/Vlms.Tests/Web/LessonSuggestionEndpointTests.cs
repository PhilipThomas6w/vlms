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
/// <see cref="LessonSuggestionEndpoints"/> on an in-memory test server, as <see cref="LessonFileEndpointTests"/>: the real
/// policies and handlers, the real <see cref="LessonSuggestionService"/> over SQLite in-memory. A suggestion's picture is
/// for its author and Approvers; a version's picture for Approvers and Admins; the upload refuses anyone else and any
/// request without an antiforgery token. Sign-in is faked by headers (<c>X-Test-User</c>, <c>X-Test-Roles</c>).
/// </summary>
public sealed class LessonSuggestionEndpointTests : IAsyncLifetime
{
    private const string UserHeader = "X-Test-User";
    private const string RolesHeader = "X-Test-Roles";
    private const int Author = 10, OtherTeacher = 11, Approver = 20;

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-suggestion-endpoint-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("lesson-pictures/suggestions/1/abc/seal.png", [1, 2]);
        await blobs.UploadAsync("lesson-pictures/R1-01/def/old.png", [3, 4]);

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
        builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        builder.Services.AddAuthorization(o => o.AddLessonSuggestionPolicies());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAntiforgery();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapLessonSuggestionEndpoints();
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
        context.LessonVersions.Add(new LessonVersion
        {
            Id = 1, LessonId = 1, Number = 1, Kind = LessonVersionKind.Converted, Title = "Lesson", Order = 1, IsActive = true,
            Body = "Text.\n", PublishedAt = DateTime.UtcNow
        });
        context.LessonVersionPictures.Add(new LessonVersionPicture { Id = 1, LessonVersionId = 1, Order = 1, FileName = "old.png", BlobKey = "lesson-pictures/R1-01/def/old.png" });
        context.LessonSuggestions.Add(new LessonSuggestion
        {
            Id = 1, LessonId = 1, RankId = 1, Kind = LessonChangeKind.Edit, Status = LessonSuggestionStatus.Draft, BaseVersionNumber = 1,
            AuthorUserId = Author, Title = "Lesson", Order = 1, Body = "Text.\n", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        context.LessonSuggestionPictures.Add(new LessonSuggestionPicture { Id = 1, LessonSuggestionId = 1, FileName = "seal.png", BlobKey = "lesson-pictures/suggestions/1/abc/seal.png" });
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
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("Test")), "Test"))
                : AuthenticateResult.NoResult());
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, int? user, string? roles, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (roles is not null)
        {
            request.Headers.Add(RolesHeader, roles);
            request.Headers.Add(UserHeader, user?.ToString() ?? "");
        }
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData(Author, "Teacher", HttpStatusCode.OK)]
    [InlineData(Approver, "Approver", HttpStatusCode.OK)]
    [InlineData(OtherTeacher, "Teacher", HttpStatusCode.Forbidden)]
    [InlineData(1, "Admin", HttpStatusCode.Forbidden)]
    [InlineData(1, "Parent", HttpStatusCode.Forbidden)]
    [InlineData(1, "SafeguardingOfficer", HttpStatusCode.Forbidden)]
    public async Task ASuggestionsPicture_IsForItsAuthorAndApprovers(int user, string roles, HttpStatusCode expected)
    {
        var response = await SendAsync(HttpMethod.Get, "/curriculum/suggestions/1/pictures/seal.png", user, roles);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Approver", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Teacher", HttpStatusCode.Forbidden)]
    [InlineData("Parent", HttpStatusCode.Forbidden)]
    [InlineData("Student", HttpStatusCode.Forbidden)]
    [InlineData("SafeguardingOfficer", HttpStatusCode.Forbidden)]
    public async Task AVersionsPicture_IsForApproversAndAdmins(string roles, HttpStatusCode expected)
    {
        var response = await SendAsync(HttpMethod.Get, "/lessons/versions/pictures/1", 1, roles);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Pictures_AreNotForAnyoneSignedOut()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/curriculum/suggestions/1/pictures/seal.png", null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/lessons/versions/pictures/1", null, null)).StatusCode);
    }

    private static MultipartFormDataContent Upload()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "seal.png");
        return form;
    }

    [Theory]
    [InlineData("Parent")]
    [InlineData("Admin")]
    [InlineData("SafeguardingOfficer")]
    public async Task Upload_IsRefusedToThoseWhoMayNotSuggest(string roles)
    {
        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/pictures", Author, roles, Upload());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Upload_SignedOut_IsUnauthorised()
    {
        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/pictures", null, null, Upload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutAnAntiforgeryToken_IsRefused()
    {
        var response = await SendAsync(HttpMethod.Post, "/curriculum/suggestions/1/pictures", Author, "Teacher", Upload());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_anchor.ConnectionString).Options, new FakeCurrentUserContext(1, Role.Admin));
        Assert.Equal(1, await context.LessonSuggestionPictures.CountAsync());
    }
}
