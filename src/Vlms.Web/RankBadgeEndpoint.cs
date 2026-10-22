using Microsoft.AspNetCore.Authorization;
using Vlms.Infrastructure.Badges;

namespace Vlms.Web;

/// <summary>
/// The rank badge image endpoint (functional.md FR-014): <c>GET /badges/{rankId}/image</c> serves the
/// badge image for one rank. Behind the <see cref="LessonFileEndpoint.PolicyName"/> policy (the lesson
/// library's roles, plus MFA), and <see cref="RankBadgeService"/> checks the role again.
///
/// Always sent with <c>nosniff</c>. SVG can carry script, so an SVG response also gets
/// <c>Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'</c>: script cannot run and
/// nothing external can load even if the SVG is opened directly.
/// </summary>
public static class RankBadgeEndpoint
{
    public const string RoutePattern = "/badges/{rankId:int}/image";

    private const string SvgContentType = "image/svg+xml";
    private const string SvgContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";

    public static IEndpointConventionBuilder MapRankBadgeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(RoutePattern, async (int rankId, RankBadgeService badges, HttpContext http, CancellationToken ct) =>
        {
            RankBadgeService.BadgeImage? image;
            try
            {
                image = await badges.OpenImageAsync(rankId, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            if (image is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.XContentTypeOptions = "nosniff";
            if (image.ContentType == SvgContentType)
            {
                http.Response.Headers["Content-Security-Policy"] = SvgContentSecurityPolicy;
            }

            return Results.Bytes(image.Content, image.ContentType);
        }).RequireAuthorization(LessonFileEndpoint.PolicyName);
}
