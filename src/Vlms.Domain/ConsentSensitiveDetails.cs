namespace Vlms.Domain;

/// <summary>
/// Split out from <see cref="ConsentRecord"/> at design review (adr/0004-sensitive-data-access-control.md):
/// a global query filter can only restrict a whole row, not mask a column within a row a role
/// otherwise needs — so the sensitive fields live in their own 1:1 entity, subject to the same
/// whole-entity query-filter restriction as <see cref="DbsCheck"/>. Visible to Admin and Safeguarding
/// Officer (with MFA), to Teachers (read only), and to the student's linked Parents (their own
/// linked children only) — amended from Admin/Safeguarding Officer only by
/// adr/0006-parent-consent-and-teacher-read-access.md (functional.md FR-015). Every materialized row
/// is still read-audited.
/// </summary>
public sealed class ConsentSensitiveDetails : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    /// <summary>Longest allowed <see cref="DietaryRequirements"/> or <see cref="SpecialEducationalNeeds"/>; the columns are unbounded text, so this is enforced in the services and on the inputs.</summary>
    public const int MaxTextLength = 4000;

    public required int Id { get; init; }
    public required int ConsentRecordId { get; init; }
    public string? EmergencyMedicalInfo { get; init; }
    /// <summary>Dietary requirements, in full (adr/0008 decision 5). At most <see cref="MaxTextLength"/> characters.</summary>
    public string? DietaryRequirements { get; init; }

    /// <summary>Special educational needs, in full (adr/0008 decision 5). At most <see cref="MaxTextLength"/> characters.</summary>
    public string? SpecialEducationalNeeds { get; init; }
    public required string EmergencyContact { get; init; }

    public ConsentRecord? ConsentRecord { get; init; }
}
