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
/// <see cref="ProposalFileEndpoint"/> on an in-memory test server: pending files reach the proposer
/// and Approvers only. Sign-in is faked by headers (<c>X-Test-Roles</c>, <c>X-Test-User</c>).
/// </summary>
public sealed class ProposalFileEndpointTests : IAsyncLifetime
{
    private const string RolesHeader = "X-Test-Roles";
    private const string UserHeader = "X-Test-User";

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-propendpoint-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("lessons/pending/10/a/Plan.pdf", [37, 80, 68, 70]);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IBlobStorage>(blobs);
        builder.Services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddScoped<ICurrentUserContext>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
            var roles = ((string?)http.Request.Headers[RolesHeader] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<Role>).ToArray();
            return new FakeCurrentUserContext(int.Parse(http.Request.Headers[UserHeader].FirstOrDefault() ?? "0"), roles);
        });
        builder.Services.AddScoped<LessonProposalService>();
        builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        builder.Services.AddAuthorization(o => o.AddProposalFilePolicy());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapProposalFileEndpoint();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "First" });
        context.AppUsers.Add(new AppUser { Id = 10, DisplayName = "Teacher", Email = "t@example.com" });
        context.LessonChangeProposals.Add(new LessonChangeProposal
        {
            Id = 1, ProposedByUserId = 10, ChangeType = LessonChangeType.Create, Status = ProposalStatus.Pending,
            SubmittedAt = DateTime.UtcNow,
            ProposedContent = System.Text.Json.JsonSerializer.Serialize(new ProposedLessonContent
            {
                RankId = 1, Code = "N", Title = "New", Order = 1, Tags = [LessonTag.Prayers], IsActive = true,
                Files = [new ProposedLessonFile { FileName = "Plan.pdf", BlobKey = "lessons/pending/10/a/Plan.pdf" }]
            })
        });
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

    private Task<HttpResponseMessage> GetAsync(string? roles, int userId, string path = "/curriculum/proposals/1/files/0")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (roles is not null)
        {
            request.Headers.Add(RolesHeader, roles);
            request.Headers.Add(UserHeader, userId.ToString());
        }

        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("Teacher", 10)]
    [InlineData("Approver", 20)]
    public async Task ProposerAndApprover_GetTheFile(string role, int userId)
    {
        var response = await GetAsync(role, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task AnotherTeacher_IsForbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("Teacher", 11)).StatusCode);

    [Theory]
    [InlineData("Student")]
    [InlineData("Parent")]
    [InlineData("Admin")]
    public async Task StudentParentAdmin_AreForbidden(string role) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(role, 30)).StatusCode);

    [Fact]
    public async Task Anonymous_IsRefused() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(null, 0)).StatusCode);

    [Fact]
    public async Task UnknownIndex_IsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("Approver", 20, "/curriculum/proposals/1/files/7")).StatusCode);
}
