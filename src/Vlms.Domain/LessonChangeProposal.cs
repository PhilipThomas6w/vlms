namespace Vlms.Domain;

public enum LessonChangeType
{
    Create,
    Edit,
    Retire
}

public enum ProposalStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// A Teacher-proposed change to a <see cref="Lesson"/> (create/edit/retire). Only the Approver role
/// decides. Rejection supports comments and resubmission via <see cref="ResubmissionOfProposalId"/>.
/// </summary>
public sealed class LessonChangeProposal
{
    public required int Id { get; init; }

    /// <summary>Null for a brand-new lesson (ChangeType == Create).</summary>
    public int? LessonId { get; init; }

    public required int ProposedByUserId { get; init; }
    public required LessonChangeType ChangeType { get; init; }
    public required string ProposedContent { get; init; }
    public required ProposalStatus Status { get; init; }
    public int? ApproverUserId { get; init; }
    public string? ApprovalComments { get; init; }
    public required DateTime SubmittedAt { get; init; }
    public DateTime? DecidedAt { get; init; }

    /// <summary>Self-referencing: chains a resubmission back to the original rejected proposal.</summary>
    public int? ResubmissionOfProposalId { get; init; }

    public Lesson? Lesson { get; init; }
    public AppUser? ProposedByUser { get; init; }
    public AppUser? ApproverUser { get; init; }
    public LessonChangeProposal? ResubmissionOfProposal { get; init; }
}
