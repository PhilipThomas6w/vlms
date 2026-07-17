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
/// </summary>
public sealed class ConsentRecord
{
    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }
    public required bool PhotoMediaConsent { get; init; }
    public required bool TransportOffsiteConsent { get; init; }
    public required bool DataSharingConsent { get; init; }
    public required ConsentStatus Status { get; init; }
    public required int SubmittedByParentId { get; init; }

    /// <summary>Safeguarding Officer or Admin — never the Approver role, which is curriculum-only.</summary>
    public int? ApprovedByUserId { get; init; }

    public required DateOnly ExpiryDate { get; init; }

    public Student? Student { get; init; }
    public ParentGuardian? SubmittedByParent { get; init; }
    public AppUser? ApprovedByUser { get; init; }
    public ConsentSensitiveDetails? SensitiveDetails { get; init; }
}
