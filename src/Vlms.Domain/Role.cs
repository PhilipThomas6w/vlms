namespace Vlms.Domain;

/// <summary>
/// Roles as application claims, not Entra groups (adr/0002-roles-as-application-claims.md).
/// The Approver role is curriculum-approval only — never conflate it with consent/safeguarding sign-off.
/// </summary>
public enum Role
{
    Admin,
    Teacher,
    Approver,
    Parent,
    Student,
    SafeguardingOfficer
}
