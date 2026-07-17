namespace Vlms.Infrastructure.Security;

/// <summary>
/// Well-known Microsoft Entra External ID (CIAM) claim types used to resolve the signed-in
/// AppUser. Shared between <see cref="EntraCurrentUserContext"/> and the OIDC
/// <c>OnTokenValidated</c> provisioning hook wired in Vlms.Web's Program.cs, so both read the
/// object id the same way.
/// </summary>
public static class EntraClaimTypes
{
    /// <summary>The long-form object identifier claim URI issued by Microsoft Entra.</summary>
    public const string ObjectId = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>The short "oid" claim, as commonly emitted by Microsoft Entra External ID (CIAM) tokens.</summary>
    public const string ObjectIdShort = "oid";
}
