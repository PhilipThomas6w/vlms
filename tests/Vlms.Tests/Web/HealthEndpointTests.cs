using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Tests.EndToEnd;
using Vlms.Tests.Infrastructure;
using Vlms.Web;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// <c>GET /health</c> (docs/operations/monitoring.md §5): the real pipeline answers 200 when the database
/// answers; a database that cannot be reached gives 503; in both cases the body is only the status word, so
/// no exception text reaches an anonymous caller.
/// </summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task RealPipeline_AnonymousGet_IsHealthy200_WithOnlyTheStatusWord()
    {
        using var factory = new VlmsWebFactory();

        var response = await factory.CreateBrowser().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnreachableDatabase_Is503_WithOnlyTheStatusWord()
    {
        await using var app = await StartAsync("Data Source=/vlms-no-such-directory/missing.db;Mode=ReadOnly");

        var response = await app.GetTestClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ThrowingCheck_Is503_AndTheExceptionTextIsNotInTheResponse()
    {
        await using var app = await StartAsync(
            "Data Source=file:vlms-health-ok;Mode=Memory;Cache=Shared",
            services => services.AddHealthChecks().AddCheck<ThrowingCheck>("throws"));

        var response = await app.GetTestClient().GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain("Host=db.internal", body);
        Assert.False(response.Headers.Contains("X-Exception"));
    }

    private static async Task<WebApplication> StartAsync(string sqliteConnectionString, Action<IServiceCollection>? more = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICurrentUserContext>(new FakeCurrentUserContext(1, Role.Admin));
        builder.Services.AddDbContext<VlmsDbContext>(o => o.UseSqlite(sqliteConnectionString));
        builder.Services.AddVlmsHealthChecks();
        more?.Invoke(builder.Services);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();

        var app = builder.Build();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapVlmsHealth();
        await app.StartAsync();
        return app;
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Host=db.internal;Password=hunter2");
    }
}
