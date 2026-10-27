using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vlms.Infrastructure;

namespace Vlms.Web;

/// <summary>
/// <c>GET /health</c> for the external uptime monitor (docs/operations/monitoring.md §5). Anonymous and
/// exempt from antiforgery. The answer is only a status code and the one word the framework's default
/// writer produces: 200 <c>Healthy</c> or 503 <c>Unhealthy</c>. No check name, description, timing or
/// exception text is written, so nothing about the database or the host reaches an unauthenticated caller
/// (the failure itself is not logged per probe either: the monitor alerts, and the journal has the
/// application's own errors). It opens a connection to PostgreSQL, so a pass proves Caddy, TLS, Kestrel and
/// the database in one request.
/// </summary>
public static class HealthEndpoint
{
    public const string Path = "/health";

    public static IServiceCollection AddVlmsHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        return services;
    }

    public static IEndpointConventionBuilder MapVlmsHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthChecks(Path, new HealthCheckOptions
        {
            // The defaults, stated: healthy 200, degraded 200, unhealthy 503; no body beyond the status word.
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous().DisableAntiforgery();

    /// <summary>Reports unhealthy when the database cannot be reached within five seconds. Never throws and never carries the exception.</summary>
    public sealed class DatabaseHealthCheck(VlmsDbContext db) : IHealthCheck
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            try
            {
                return await db.Database.CanConnectAsync(limit.Token)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy();
            }
            catch (Exception)
            {
                return HealthCheckResult.Unhealthy();
            }
        }
    }
}
