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
/// </list>
/// There is no separate offline rendering: the lesson page itself is plain server-rendered HTML, and that is what
/// "Save to this device" saves, with its pictures (decision 7, as amended by Philip, 26/10/2026).
/// </summary>
public static class LessonPageEndpoint
{
    public const string TeachersInformationPolicyName = "RequireTeachersInformation";
    public const string PictureRoutePattern = "/lessons/pictures/{id:int}";
    public const string TeachersInformationPictureRoutePattern = "/lessons/teachers-information/pictures/{id:int}";

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
