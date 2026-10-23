namespace Vlms.Domain;

/// <summary>
/// Many-to-many join between <see cref="Student"/> and <see cref="ParentGuardian"/>.
/// Created only by an Admin: registering a child directly, linking a guardian, or approving a
/// parent's registration application after a Safeguarding Officer's review — never by parent
/// self-service and, since 22/10/2026, never by a Teacher (adr/0008 answer 4; data-design.md
/// "Guardian link verification").
/// </summary>
public sealed class StudentGuardianLink
{
    public required int StudentId { get; init; }
    public required int ParentGuardianId { get; init; }
    public required int CreatedByUserId { get; init; }

    /// <summary>The guardian's relationship to the child, as given in a parent's registration application. Null for links made by staff.</summary>
    public string? Relationship { get; init; }

    public Student? Student { get; init; }
    public ParentGuardian? ParentGuardian { get; init; }
    public AppUser? CreatedByUser { get; init; }
}
