using Microsoft.AspNetCore.Authorization;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;

namespace Vlms.Web;

/// <summary>
/// <c>GET /curriculum/proposals/{proposalId}/files/{index}</c> (FR-007): opens a file of a lesson
/// proposal, including files not yet live. The policy admits Teachers and Approvers (plus MFA);
/// <see cref="LessonProposalService.OpenProposalFileAsync"/> then limits a Teacher to their own
/// proposals. Students, parents and everyone else never reach it.
/// </summary>
public static class ProposalFileEndpoint
{
    public const string PolicyName = "RequireProposalFiles";
    public const string RoutePattern = "/curriculum/proposals/{proposalId:int}/files/{index:int}";

    public static void AddProposalFilePolicy(this AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([Role.Teacher, Role.Approver]));
            policy.Requirements.Add(new MfaRequirement());
        });

    public static IEndpointConventionBuilder MapProposalFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(RoutePattern, async (int proposalId, int index, LessonProposalService proposals, HttpContext http, CancellationToken ct) =>
        {
            LessonFileContent? file;
            try
            {
                file = await proposals.OpenProposalFileAsync(proposalId, index, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            return file is null ? Results.NotFound() : LessonFileEndpoint.ToResult(file, http);
        }).RequireAuthorization(PolicyName);
}
