namespace Vlms.Domain;

/// <summary>
/// Roles as application claims, not Entra groups (adr/0002-roles-as-application-claims.md).
/// The Approver role is curriculum-approval only — never conflate it with consent/safeguarding sign-off.
/// Admin, Teacher, Parent, Student and SafeguardingOfficer are held per chapter; Approver and
/// NationalAdministrator are national (<see cref="RoleScope"/>, docs/adr/0010-chapters.md).
/// Stored as its integer value, so new members are only ever appended.
/// </summary>
public enum Role
{
    Admin,
    Teacher,
    Approver,
    Parent,
    Student,
    SafeguardingOfficer,

    /// <summary>
    /// Runs the platform (docs/adr/0010-chapters.md): creates and edits chapters, appoints and
    /// removes each chapter's Admins, and manages the national roles. Sees no chapter's children,
    /// consent, DBS or application records unless they also hold a role in that chapter.
    /// </summary>
    NationalAdministrator
}

/// <summary>Which roles are held per chapter and which nationally (docs/adr/0010-chapters.md).</summary>
public static class RoleScope
{
    /// <summary>The roles held in one chapter: Admin, Teacher, Parent, Student, Safeguarding Officer.</summary>
    public static bool IsChapterRole(this Role role) =>
        role is Role.Admin or Role.Teacher or Role.Parent or Role.Student or Role.SafeguardingOfficer;

    /// <summary>The national roles: Approver (curriculum only) and National Administrator.</summary>
    public static bool IsNationalRole(this Role role) => !role.IsChapterRole();
}
