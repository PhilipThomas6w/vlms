namespace Vlms.Domain;

public enum ConsentStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// Annual parental consent. Deliberately contains no sensitive fields (see
/// <see cref="ConsentSensitiveDetails"/> and adr/0004-sensitive-data-access-control.md) —
/// <see cref="Status"/>/<see cref="ExpiryDate"/> must stay readable by Teacher to enforce the
/// consent-blocks-completion rule (FR-003), so this entity is NOT subject to the sensitive-data
/// query filter.
///
/// Each row is one submission (functional.md FR-015, adr/0006-parent-consent-and-teacher-read-access.md):
/// a parent's update after approval, or a resubmission after rejection, is a new Pending row, never
/// an overwrite of an earlier one, so the history of what was consented to and decided is kept.
/// </summary>
public sealed class ConsentRecord
{
    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }
    public required bool PhotoMediaConsent { get; init; }
    /// <summary>Consent to transport between sites (adr/0008 decision 5); separate from <see cref="OffsiteTripConsent"/>.</summary>
    public required bool TransportConsent { get; init; }

    /// <summary>Consent to off-site trips and outings (adr/0008 decision 5).</summary>
    public required bool OffsiteTripConsent { get; init; }

    public required bool DataSharingConsent { get; init; }
    public required ConsentStatus Status { get; init; }
    public required int SubmittedByParentId { get; init; }

    /// <summary>Safeguarding Officer or Admin — never the Approver role, which is curriculum-only.</summary>
    public int? ApprovedByUserId { get; init; }

    public required DateOnly ExpiryDate { get; init; }

    /// <summary>When the submission was made. Null only for rows created before FR-015.</summary>
    public DateTime? SubmittedAt { get; init; }

    /// <summary>
    /// The <see cref="AppUser"/> who entered the submission: the linked parent themselves, or an
    /// Admin/Safeguarding Officer entering a paper form on the parent's behalf. Null only for rows
    /// created before FR-015.
    /// </summary>
    public int? SubmittedByUserId { get; init; }

    /// <summary>
    /// The student's name as given on the form (functional.md FR-013). Applied to
    /// <see cref="Student.Name"/> only when the submission is approved. Null when the form did not
    /// carry personal details (the Admin/Safeguarding Officer "on behalf of" path).
    /// </summary>
    public string? StudentName { get; init; }

    /// <summary>The student's date of birth as given on the form — see <see cref="StudentName"/>.</summary>
    public DateOnly? StudentDateOfBirth { get; init; }

    /// <summary>When an Admin/Safeguarding Officer approved or rejected the submission.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>
    /// The reviewer's comments. Mandatory on rejection (functional.md FR-015) so the parent knows
    /// what to correct; optional on approval.
    /// </summary>
    public string? DecisionComments { get; init; }

    public Student? Student { get; init; }
    public ParentGuardian? SubmittedByParent { get; init; }
    public AppUser? ApprovedByUser { get; init; }
    public AppUser? SubmittedByUser { get; init; }
    public ConsentSensitiveDetails? SensitiveDetails { get; init; }
}
