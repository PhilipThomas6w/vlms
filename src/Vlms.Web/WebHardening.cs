using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Vlms.Web;

/// <summary>
/// Production web hardening (STATE.md "Production web hardening"): host filtering, HSTS lifetime,
/// and rate limiting on the unauthenticated account forms. Kept out of Program.cs so tests can
/// exercise the same registration against a test server.
///
/// <b>Allowed hosts.</b> <c>AllowedHosts</c> (ASP.NET Core host filtering) must name the real
/// host(s) in Production — startup fails if it is missing or <c>*</c>. vlms-web.service sets it to
/// <c>www.varangians.co.uk</c>; Caddy redirects the bare domain there, so no other host reaches
/// Kestrel.
///
/// <b>HSTS.</b> <c>Hsts:MaxAgeDays</c>, default 365 days (the framework default is 30). Subdomains
/// and preload stay off: other <c>varangians.co.uk</c> names (e.g. Microsoft 365's autodiscover)
/// aren't this app's to force onto HTTPS, and preload is hard to undo.
///
/// <b>Rate limiting.</b> <see cref="AccountFormsPolicy"/> limits form submissions (POSTs) to the
/// sign-in, registration, password-reset, confirmation-resend and invite-accept pages, per client IP
/// and page: <c>RateLimiting:AccountFormPostsPerMinute</c>, default 20 per minute. Page loads (GETs)
/// aren't limited. This complements Identity's per-account lockout (VlmsIdentityPolicy) by slowing
/// guessing across many accounts and stopping the email-sending forms from being used to flood
/// inboxes. The default leaves room for a group signing in together from one network (e.g. the
/// church hall's Wi-Fi, which shares one public IP). The client IP is the real one because
/// <c>UseForwardedHeaders</c> runs earlier in the pipeline. A rejected request gets 429 with a short
/// plain-text body, which also stops the status-code-pages middleware rendering "not found" for it.
/// </summary>
public static class WebHardening
{
    public const string AccountFormsPolicy = "account-forms";
    public const string HstsMaxAgeDaysKey = "Hsts:MaxAgeDays";
    public const string AccountFormPostsPerMinuteKey = "RateLimiting:AccountFormPostsPerMinute";
    public const int DefaultHstsMaxAgeDays = 365;
    public const int DefaultAccountFormPostsPerMinute = 20;
    public const string RejectionMessage = "Too many attempts from your network. Please wait a minute and try again.";

    public static IServiceCollection AddVlmsWebHardening(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            RequireExplicitAllowedHosts(configuration["AllowedHosts"]);
        }

        var maxAgeDays = configuration.GetValue(HstsMaxAgeDaysKey, DefaultHstsMaxAgeDays);
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(maxAgeDays);
            options.IncludeSubDomains = false;
            options.Preload = false;
        });

        var permitsPerMinute = configuration.GetValue(AccountFormPostsPerMinuteKey, DefaultAccountFormPostsPerMinute);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync(RejectionMessage, ct);
            };
            options.AddPolicy(AccountFormsPolicy, httpContext =>
                HttpMethods.IsPost(httpContext.Request.Method)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        $"{httpContext.Connection.RemoteIpAddress}|{httpContext.Request.Path.Value?.ToLowerInvariant()}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        })
                    : RateLimitPartition.GetNoLimiter("page-load"));
        });

        return services;
    }

    /// <summary>Throws unless <paramref name="allowedHosts"/> names at least one host and none is the <c>*</c> wildcard.</summary>
    public static void RequireExplicitAllowedHosts(string? allowedHosts)
    {
        var hosts = (allowedHosts ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Contains("*"))
        {
            throw new InvalidOperationException(
                "AllowedHosts must list the site's real host name(s) in Production, not '*' (see deploy/systemd/vlms-web.service).");
        }
    }
}
