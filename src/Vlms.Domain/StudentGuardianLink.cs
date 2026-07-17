namespace Vlms.Domain;

/// <summary>
/// Many-to-many join between <see cref="Student"/> and <see cref="ParentGuardian"/>.
/// Created only by Admin/Teacher at student registration — never by parent self-service
/// (see data-design.md "Guardian link verification").
/// </summary>
public sealed class StudentGuardianLink
{
    public required int StudentId { get; init; }
    public required int ParentGuardianId { get; init; }
    public required int CreatedByUserId { get; init; }

    public Student? Student { get; init; }
    public ParentGuardian? ParentGuardian { get; init; }
    public AppUser? CreatedByUser { get; init; }
}
