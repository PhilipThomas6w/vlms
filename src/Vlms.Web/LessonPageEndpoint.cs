using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;

namespace Vlms.Web;

/// <summary>
/// The endpoints of app-hosted lessons (docs/adr/0012-app-hosted-lessons.md), alongside the lesson page itself
/// (<c>Components/Pages/Lessons/LessonText.razor</c>):
/// <list type="bullet">
/// <item><c>GET /lessons/pictures/{id}</c>: a lesson picture by id only, behind the lesson-library policy
/// (<see cref="LessonFileEndpoint.PolicyName"/>: library role plus MFA), the service checking the role again.</item>
/// <item><c>GET /lessons/teachers-information/pictures/{id}</c>: a Teacher's Information picture by id only, behind
/// <see cref="TeachersInformationPolicyName"/> (Teacher, Admin or National Administrator, plus MFA), the service checking
/// again.</item>
/// <item><c>GET /lessons/{code}/offline</c> (decision 7): the lesson as one plain HTML page that needs no live
/// connection, so a device can save it with its pictures (FR-023). Library policy. Never carries the Teacher's
/// Information. Sent with a Content-Security-Policy that allows no script at all, only its own inline style and
/// same-origin pictures, as a second guard behind the Markdown renderer's (raw HTML disabled).</item>
/// </list>
/// </summary>
public static class LessonPageEndpoint
{
    public const string TeachersInformationPolicyName = "RequireTeachersInformation";
    public const string PictureRoutePattern = "/lessons/pictures/{id:int}";
    public const string TeachersInformationPictureRoutePattern = "/lessons/teachers-information/pictures/{id:int}";
    public const string OfflineRoutePattern = "/lessons/{code}/offline";

    /// <summary>The offline page's Content-Security-Policy: no script, no frames, no forms, pictures from this site only.</summary>
    public const string OfflineContentSecurityPolicy =
        "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static void AddTeachersInformationPolicy(this AuthorizationOptions options) =>
        options.AddPolicy(TeachersInformationPolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([.. LessonLibraryService.TeachersInformationRoles]));
            policy.Requirements.Add(new MfaRequirement());
        });

    public static void MapLessonPageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(PictureRoutePattern, (int id, LessonLibraryService library, HttpContext http, CancellationToken ct) =>
            Serve(() => library.OpenPictureAsync(id, ct), http))
            .RequireAuthorization(LessonFileEndpoint.PolicyName);

        endpoints.MapGet(TeachersInformationPictureRoutePattern, (int id, LessonLibraryService library, HttpContext http, CancellationToken ct) =>
            Serve(() => library.OpenTeachersInformationPictureAsync(id, ct), http))
            .RequireAuthorization(TeachersInformationPolicyName);

        endpoints.MapGet(OfflineRoutePattern, async (string code, LessonLibraryService library, HttpContext http, CancellationToken ct) =>
        {
            LessonPage? page;
            try
            {
                page = await library.GetLessonPageAsync(code, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            if (page is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers["Content-Security-Policy"] = OfflineContentSecurityPolicy;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, no-cache";
            return Results.Content(OfflineHtml(page), "text/html; charset=utf-8");
        }).RequireAuthorization(LessonFileEndpoint.PolicyName);
    }

    private static async Task<IResult> Serve(Func<Task<LessonFileContent?>> open, HttpContext http)
    {
        LessonFileContent? file;
        try
        {
            file = await open();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }

        return file is null ? Results.NotFound() : LessonFileEndpoint.ToResult(file, http);
    }

    /// <summary>The self-contained page: the lesson title and text, with its own small stylesheet, readable on a phone and in print.</summary>
    internal static string OfflineHtml(LessonPage page)
    {
        var title = WebUtility.HtmlEncode(page.Title);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en-GB\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(title).Append("</title>\n<style>\n").Append(OfflineCss).Append("</style>\n</head>\n<body>\n")
            .Append("<nav><a href=\"/lessons\">Lesson library</a></nav>\n<main>\n")
            .Append("<p class=\"rank\">").Append(WebUtility.HtmlEncode(page.Rank)).Append("</p>\n")
            .Append("<h1>").Append(title).Append("</h1>\n")
            .Append("<article class=\"lesson-text\">\n").Append(page.Html).Append("</article>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private const string OfflineCss = """
        :root { color-scheme: light; }
        body { margin: 0; padding: 1rem; background: #F7F8FA; color: #172033; font: 1.0625rem/1.6 "Source Sans 3", "Segoe UI", system-ui, -apple-system, Roboto, sans-serif; }
        nav, main { max-width: 68ch; margin: 0 auto; }
        nav a { display: inline-flex; align-items: center; min-height: 2.75rem; }
        a { color: #1F45A3; }
        a:focus-visible { outline: 3px solid #1F45A3; outline-offset: 2px; }
        h1, h2, h3, h4 { font-family: "Alegreya", "Iowan Old Style", "Palatino Linotype", Georgia, serif; color: #14254F; line-height: 1.25; }
        h1 { font-size: 1.953rem; margin: 0 0 1rem; }
        .rank { margin: 1rem 0 0; color: #4A5568; }
        img { display: block; max-width: 100%; height: auto; margin: 1rem 0; }
        table { display: block; overflow-x: auto; border-collapse: collapse; margin: 1rem 0; }
        th, td { border: 1px solid #6B7689; padding: 0.375rem 0.625rem; text-align: left; vertical-align: top; }
        @media print {
            body { background: #FFFFFF; padding: 0; font-size: 11pt; }
            nav { display: none; }
            main { max-width: none; }
            a { color: inherit; }
            img { max-height: 18cm; break-inside: avoid; }
            h2, h3 { break-after: avoid; }
            table { display: table; }
        }

        """;
}
