using Microsoft.AspNetCore.Mvc;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Web;

/// <summary>
/// The chapter switcher's form target (FR-021, docs/adr/0010-chapters.md): <c>POST /chapters/switch</c>
/// with the chosen chapter and the page to return to. A plain form post (antiforgery-checked, as
/// every form-bound minimal API endpoint is once <c>UseAntiforgery</c> runs) rather than an
/// interactive component, so it works from the static layout and the next page load starts fresh
/// in the new chapter. <see cref="ChapterService.SwitchToAsync"/> refuses a chapter the caller holds
/// no role in. Only a local return path is followed (no open redirect).
/// </summary>
public static class ChapterSwitchEndpoint
{
    public const string RoutePattern = "/chapters/switch";

    public static IEndpointConventionBuilder MapChapterSwitchEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(RoutePattern, async ([FromForm] int chapterId, [FromForm] string? returnUrl, ChapterService chapters, CancellationToken ct) =>
        {
            try
            {
                await chapters.SwitchToAsync(chapterId, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            var target = IsLocal(returnUrl) ? returnUrl! : "/";
            return Results.LocalRedirect(target);
        }).RequireAuthorization("RequireSignedIn");

    // A path on this site: starts with one "/" (not "//" or "/\", which browsers treat as another host).
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
