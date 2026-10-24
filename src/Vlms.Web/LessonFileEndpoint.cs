using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;

namespace Vlms.Web;

/// <summary>
/// The lesson-file download endpoint (functional.md FR-006): <c>GET /lessons/files/{id}</c> serves
/// one <see cref="LessonFile"/> by its id, never by a blob key from the URL. Behind the
/// <see cref="PolicyName"/> policy (signed in, one of <see cref="LessonLibraryService.LibraryRoles"/>,
/// plus the MFA requirement every policy carries), and <see cref="LessonLibraryService"/> checks
/// the role again. PDFs and images are sent inline; anything else as an attachment, always with
/// <c>nosniff</c> so the declared type is what the browser uses.
/// </summary>
public static class LessonFileEndpoint
{
    public const string PolicyName = "RequireLessonLibrary";
    public const string RoutePattern = "/lessons/files/{id:int}";
    public const string SavedRoutePattern = "/lessons/saved";

    public static void AddLessonLibraryPolicy(this AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([.. LessonLibraryService.LibraryRoles]));
            policy.Requirements.Add(new MfaRequirement());
        });

    public static IEndpointConventionBuilder MapLessonFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(RoutePattern, async (int id, LessonLibraryService library, HttpContext http, CancellationToken ct) =>
        {
            LessonFileContent? file;
            try
            {
                file = await library.OpenFileAsync(id, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            if (file is null)
            {
                return Results.NotFound();
            }

            return ToResult(file, http);
        }).RequireAuthorization(PolicyName);

    /// <summary>
    /// <c>GET /lessons/saved?id=1&amp;id=2</c> (FR-023): the current lessons and files for the lesson ids a
    /// device has saved, so it can drop what is gone and fetch what is new. Same policy and role check as
    /// the file endpoint; ids that are unknown or retired are left out of the answer.
    /// </summary>
    public static IEndpointConventionBuilder MapSavedLessonsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(SavedRoutePattern, async (int[] id, LessonLibraryService library, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await library.GetSavedLessonsAsync(id, ct));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).RequireAuthorization(PolicyName);

    /// <summary>Inline for pdf/images, attachment for everything else, always nosniff.</summary>
    internal static IResult ToResult(LessonFileContent file, HttpContext http)
    {
        var disposition = new ContentDispositionHeaderValue(LessonFileTypes.IsInline(file.FileName) ? "inline" : "attachment");
        disposition.SetHttpFileName(file.FileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(file.Content, LessonFileTypes.ContentTypeFor(file.FileName));
    }
}
