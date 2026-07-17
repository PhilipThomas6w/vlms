namespace Vlms.Domain;

/// <summary>
/// A single user may hold more than one role (e.g. a Teacher who is also the Approver) —
/// hence a separate row per (UserId, Role) pair rather than a single-valued column on AppUser.
/// </summary>
public sealed class UserRole
{
    public required int UserId { get; init; }
    public required Role Role { get; init; }

    public AppUser? User { get; init; }
}
