namespace Vlms.Domain;

/// <summary>
/// A single user may hold more than one role (e.g. a Teacher who is also the Approver) —
/// hence a separate row per (UserId, Role, ChapterId) rather than a single-valued column on AppUser.
/// Roles are per chapter (docs/adr/0010-chapters.md): a chapter role
/// (<see cref="RoleScope.IsChapterRole"/>) always carries the chapter it is held in, and a national
/// role (<see cref="Role.Approver"/>, <see cref="Role.NationalAdministrator"/>) never does.
/// </summary>
public sealed class UserRole
{
    /// <summary>Database-generated surrogate key; (UserId, Role, ChapterId) is unique.</summary>
    public int Id { get; init; }

    public required int UserId { get; init; }
    public required Role Role { get; init; }

    /// <summary>The chapter a chapter role is held in; null for a national role.</summary>
    public int? ChapterId { get; init; }

    public AppUser? User { get; init; }
}
