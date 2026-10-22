using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Progress;

namespace Vlms.Web;

/// <summary>
/// The certificate endpoint (functional.md FR-010): <c>GET /certificates/{id}</c> serves one
/// certificate PDF by its <see cref="Vlms.Domain.Certificate.Id"/>, inline so it opens in the
/// browser, or as an attachment with <c>?download=1</c>. Behind the <see cref="PolicyName"/> policy
/// (one of <see cref="CertificateAccessService.CertificateRoles"/> plus MFA); the per-certificate
/// check is <see cref="CertificateAccessService"/>'s, and any refusal is a 404 so nothing reveals
/// whether a certificate exists. Always <c>nosniff</c>.
/// </summary>
public static class CertificateEndpoint
{
    public const string PolicyName = "RequireCertificateAccess";
    public const string RoutePattern = "/certificates/{id:int}";

    public static string ViewUrl(int certificateId) => $"/certificates/{certificateId}";
    public static string DownloadUrl(int certificateId) => $"/certificates/{certificateId}?download=1";

    public static void AddCertificateAccessPolicy(this AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, policy =>
        {
            policy.Requirements.Add(new AnyRoleRequirement([.. CertificateAccessService.CertificateRoles]));
            policy.Requirements.Add(new MfaRequirement());
        });

    public static IEndpointConventionBuilder MapCertificateEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(RoutePattern, async (int id, string? download, CertificateAccessService certificates, HttpContext http, CancellationToken ct) =>
        {
            var file = await certificates.OpenAsync(id, ct);
            if (file is null)
            {
                return Results.NotFound();
            }

            var disposition = new ContentDispositionHeaderValue(download == "1" ? "attachment" : "inline");
            disposition.SetHttpFileName(file.FileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(file.Content, "application/pdf");
        }).RequireAuthorization(PolicyName);
}
