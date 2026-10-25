using Vlms.Infrastructure.Calendar;

namespace Vlms.Web;

/// <summary>
/// The private calendar subscription (functional.md FR-022): <c>GET /calendar/feed/{secret}.ics</c>,
/// fetched by a person's calendar app without signing in. The secret in the address is the only
/// credential; <see cref="CalendarFeedService.BuildFeedAsync"/> serves the chapter's events only while
/// the link exists and its owner still belongs to the chapter, and answers 404 otherwise, the same
/// for an unknown, replaced or withdrawn link.
/// </summary>
public static class CalendarFeedEndpoint
{
    public const string RoutePattern = "/calendar/feed/{secret}.ics";

    public static IEndpointConventionBuilder MapCalendarFeedEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(RoutePattern, async (string secret, CalendarFeedService feeds, HttpContext http, CancellationToken ct) =>
        {
            var ics = await feeds.BuildFeedAsync(secret, ct);
            if (ics is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Text(ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous();

    /// <summary>The chapter's public link to its parish events: <c>GET /calendar/chapter/{key}.ics</c>, 404 unless the link exists and its chapter is open.</summary>
    public const string PublicRoutePattern = "/calendar/chapter/{key}.ics";

    public static IEndpointConventionBuilder MapPublicCalendarFeedEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(PublicRoutePattern, async (string key, CalendarFeedService feeds, HttpContext http, CancellationToken ct) =>
        {
            var ics = await feeds.BuildPublicFeedAsync(key, ct);
            if (ics is null)
            {
                return Results.NotFound();
            }

            // Public parish events: a short cache is fine, and a replaced or stopped link ends within it.
            http.Response.Headers.CacheControl = "public, max-age=900";
            return Results.Text(ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous();
}
