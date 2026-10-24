namespace Vlms.Domain;

/// <summary>
/// The sensitive part of a <see cref="StudentApplication"/>: the consent form's emergency, medical,
/// dietary and special educational needs details, plus every piece of free text exchanged during
/// review (the Safeguarding Officer's request for more information, the parent's answer, and the
/// decision comments), since an answer about special educational needs is itself special-category
/// data. Split out for the same reason as <see cref="ConsentSensitiveDetails"/>
/// (adr/0004: a query filter restricts whole rows, never columns).
///
/// Visible, through an EF Core global query filter, only to Admins and Safeguarding Officers with
/// MFA enabled and to the applicant themself; never to Teachers, Approvers or other parents. Every
/// materialized row is written to <see cref="SensitiveDataAccessLog"/> by the read-audit
/// interceptor, as for <see cref="ConsentSensitiveDetails"/>. On approval the consent fields are
/// copied into a new <see cref="ConsentSensitiveDetails"/> row; this row is kept as the record of
/// what was reviewed.
/// </summary>
public sealed class StudentApplicationSensitiveDetails : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int StudentApplicationId { get; init; }

    public required string EmergencyContact { get; init; }
    public string? EmergencyMedicalInfo { get; init; }

    /// <summary>At most <see cref="ConsentSensitiveDetails.MaxTextLength"/> characters.</summary>
    public string? DietaryRequirements { get; init; }

    /// <summary>At most <see cref="ConsentSensitiveDetails.MaxTextLength"/> characters.</summary>
    public string? SpecialEducationalNeeds { get; init; }

    /// <summary>The Safeguarding Officer's latest request for more information.</summary>
    public string? InformationRequest { get; init; }

    /// <summary>The parent's answer to <see cref="InformationRequest"/>, given when resubmitting.</summary>
    public string? ParentResponse { get; init; }

    /// <summary>Comments on the decision: mandatory when rejecting at either stage, optional on approval.</summary>
    public string? DecisionComments { get; init; }

    public StudentApplication? StudentApplication { get; init; }
}
