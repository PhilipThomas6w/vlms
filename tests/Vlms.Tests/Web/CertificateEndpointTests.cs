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
using Vlms.Infrastructure.Progress;
using Vlms.Tests.Infrastructure;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Exercises <see cref="CertificateEndpoint"/> on an in-memory test server, same approach as
/// <see cref="LessonFileEndpointTests"/>: the real policy and handlers, the real
/// <see cref="CertificateAccessService"/> over SQLite in-memory. The caller is user 5 (a Teacher
/// or Admin here, so the per-certificate check passes); the per-certificate matrix is covered by
/// <c>CertificateAccessServiceTests</c>.
/// </summary>
public sealed class CertificateEndpointTests : IAsyncLifetime
{
    private const string RolesHeader = "X-Test-Roles";

    private SqliteConnection _anchor = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:vlms-certendpoint-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        var blobs = new InMemoryBlobStorage();
        await blobs.UploadAsync("certificates/100/1.pdf", [37, 80, 68, 70]);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IBlobStorage>(blobs);
        builder.Services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddScoped<ICurrentUserContext>(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
            var roles = http.Request.Headers[RolesHeader].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<Role>).ToArray();
            return new FakeCurrentUserContext(5, roles);
        });
        builder.Services.AddScoped<CertificateAccessService>();
        builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
        builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();
        builder.Services.AddAuthorization(o => o.AddCertificateAccessPolicy());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapCertificateEndpoint();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var context = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options,
            new FakeCurrentUserContext(1, Role.Admin));
        context.Database.EnsureCreated();
        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "First" });
        context.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Active", Order = 1, IsActive = true });
        context.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin", Email = "a@example.com" });
        context.Students.Add(new Student
        {
            Id = 100, Name = "Alex", DateOfBirth = new DateOnly(2012, 1, 1), CurrentRankId = 1,
            Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
        });
        context.StudentLessonCompletions.Add(new StudentLessonCompletion { Id = 1, StudentId = 100, LessonId = 1, CompletedByUserId = 1, CompletedAt = DateTime.UtcNow, IsReversed = false });
        context.Certificates.Add(new Certificate { Id = 1, StudentLessonCompletionId = 1, GeneratedAt = DateTime.UtcNow, BlobKey = "certificates/100/1.pdf" });
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

    private Task<HttpResponseMessage> GetAsync(string url, string? roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (roles is not null)
        {
            request.Headers.Add(RolesHeader, roles);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Get_AsTeacher_ServesThePdfInlineWithNoSniff()
    {
        var response = await GetAsync("/certificates/1", "Teacher");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal([37, 80, 68, 70], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Get_WithDownloadFlag_ServesAnAttachment()
    {
        var response = await GetAsync("/certificates/1?download=1", "Admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("certificate-1.pdf", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Get_UnknownId_IsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/certificates/999", "Teacher")).StatusCode);

    [Fact]
    public async Task Get_AsParentWithNoLink_IsNotFoundNotForbidden() =>
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/certificates/1", "Parent")).StatusCode);

    [Fact]
    public async Task Get_AsApprover_IsRefusedByThePolicy() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/certificates/1", "Approver")).StatusCode);

    [Fact]
    public async Task Get_Anonymous_IsNotServed()
    {
        var status = (await GetAsync("/certificates/1", null)).StatusCode;

        Assert.NotEqual(HttpStatusCode.OK, status);
    }
}
