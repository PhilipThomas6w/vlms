using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;

namespace Vlms.Web;

/// <summary>
/// The endpoints of lesson suggestions (FR-007, docs/adr/0013):
/// <list type="bullet">
/// <item><c>POST /curriculum/suggestions/{id}/pictures</c>: a picture inserted in the editor, sent by the editor's
/// script as a form with the file (field <c>file</c>) and the antiforgery token in the <c>RequestVerificationToken</c>
/// header (minimal APIs binding <see cref="IFormFile"/> require antiforgery validation). Behind
/// <see cref="SuggesterPolicyName"/>; <see cref="LessonSuggestionService.UploadPictureAsync"/> limits it to the author's
/// own draft. Answers <c>{ fileName, url }</c>, or 400 with a message people can read.</item>
/// <item><c>GET /curriculum/suggestions/{id}/pictures/{fileName}</c>: a suggestion's picture, for its author and
/// Approvers only (the service decides).</item>
/// <item><c>GET /lessons/versions/pictures/{id}</c>: a version's picture, behind <see cref="HistoryPolicyName"/>
/// (Approvers and Admins), the service checking again.</item>
/// </list>
/// </summary>
public static class LessonSuggestionEndpoints
{
    public const string SuggesterPolicyName = "RequireLessonSuggester";
    public const string HistoryPolicyName = "RequireLessonHistory";
    public const string UploadRoutePattern = "/curriculum/suggestions/{id:int}/pictures";
    public const string SuggestionPictureRoutePattern = "/curriculum/suggestions/{id:int}/pictures/{fileName}";
    public const string VersionPictureRoutePattern = "/lessons/versions/pictures/{id:int}";

    public static void AddLessonSuggestionPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(SuggesterPolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([.. LessonSuggestionService.SuggesterRoles]));
            policy.Requirements.Add(new MfaRequirement());
        });
        options.AddPolicy(HistoryPolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([.. LessonSuggestionService.HistoryRoles]));
            policy.Requirements.Add(new MfaRequirement());
        });
    }

    public static void MapLessonSuggestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(UploadRoutePattern, async (int id, IFormFile file, LessonSuggestionService suggestions, CancellationToken ct) =>
        {
            try
            {
                await using var stream = file.OpenReadStream();
                var picture = await suggestions.UploadPictureAsync(id, file.FileName, stream, ct);
                return Results.Ok(new { fileName = picture.FileName, url = picture.Url });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization(SuggesterPolicyName);

        endpoints.MapGet(SuggestionPictureRoutePattern, (int id, string fileName, LessonSuggestionService suggestions, HttpContext http, CancellationToken ct) =>
            Serve(() => suggestions.OpenSuggestionPictureAsync(id, fileName, ct), http))
            .RequireAuthorization(SuggesterPolicyName);

        endpoints.MapGet(VersionPictureRoutePattern, (int id, LessonSuggestionService suggestions, HttpContext http, CancellationToken ct) =>
            Serve(() => suggestions.OpenVersionPictureAsync(id, ct), http))
            .RequireAuthorization(HistoryPolicyName);
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
}
