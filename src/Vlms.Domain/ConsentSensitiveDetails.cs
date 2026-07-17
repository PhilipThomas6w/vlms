namespace Vlms.Domain;

/// <summary>
/// Split out from <see cref="ConsentRecord"/> at design review (adr/0004-sensitive-data-access-control.md):
/// a global query filter can only restrict a whole row, not mask a column within a row a role
/// otherwise needs — so the sensitive fields live in their own 1:1 entity, subject to the same
/// whole-entity restriction as <see cref="DbsCheck"/> (visible only to Admin and Safeguarding Officer).
/// </summary>
public sealed class ConsentSensitiveDetails
{
    public required int Id { get; init; }
    public required int ConsentRecordId { get; init; }
    public string? EmergencyMedicalInfo { get; init; }
    public string? DietarySEN { get; init; }
    public required string EmergencyContact { get; init; }

    public ConsentRecord? ConsentRecord { get; init; }
}
