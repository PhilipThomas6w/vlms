namespace Vlms.Domain;

/// <summary>Where a parent's application to register a child stands (functional.md FR-004, adr/0008 answer 4).</summary>
public enum StudentApplicationStatus
{
    /// <summary>Submitted or resubmitted; waiting for a Safeguarding Officer (stage 1).</summary>
    AwaitingSafeguardingReview,

    /// <summary>A Safeguarding Officer asked the parent for more information; waiting for the parent to resubmit.</summary>
    MoreInformationRequested,

    /// <summary>Passed stage 1; waiting for an Admin's final approval (stage 2).</summary>
    AwaitingAdminApproval,

    /// <summary>An Admin approved it: the <see cref="Student"/>, link and consent record exist.</summary>
    Approved,

    /// <summary>Rejected at either stage. A rejected application never blocks a fresh one.</summary>
    Rejected
}

/// <summary>
/// A parent's application to register one child (functional.md FR-004 as amended 22/10/2026,
/// adr/0008 answers 1, 4, 5, 6). One row per child; a parent registering several children at once
/// gets one row each, sharing the parent details.
///
/// <b>Nothing is created from this until final approval.</b> No <see cref="Student"/>,
/// <see cref="StudentGuardianLink"/>, <see cref="ParentGuardian"/>, <see cref="ConsentRecord"/> or
/// Parent role exists because of an application until an Admin approves it at stage 2, after a
/// Safeguarding Officer has approved it at stage 1. The approval is the Admin action the
/// <see cref="StudentGuardianLink"/> constraint requires.
///
/// The sensitive fields (emergency contact, medical, dietary, special educational needs) and all
/// free text exchanged during review live in <see cref="StudentApplicationSensitiveDetails"/>, which is
/// restricted and read-audited like <see cref="ConsentSensitiveDetails"/>. This row holds only what
/// is needed to list and route applications.
/// </summary>
public sealed class StudentApplication : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md). Left at 0, it is set on save to the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public const int MaxShortTextLength = 200;
    public const int MaxAddressLength = 1000;

    public required int Id { get; init; }

    /// <summary>The signed-in account that applied. Becomes the <see cref="ParentGuardian.AppUserId"/> on approval.</summary>
    public required int ApplicantUserId { get; init; }

    public required string ParentPhone { get; init; }
    public required string ParentPostalAddress { get; init; }

    /// <summary>The applicant's relationship to the child (for example mother, grandfather, foster carer).</summary>
    public required string Relationship { get; init; }

    public required string ChildName { get; init; }
    public required DateOnly ChildDateOfBirth { get; init; }

    public required bool PhotoMediaConsent { get; init; }
    public required bool TransportConsent { get; init; }
    public required bool OffsiteTripConsent { get; init; }
    public required bool DataSharingConsent { get; init; }

    public required StudentApplicationStatus Status { get; init; }

    /// <summary>When the application was first submitted.</summary>
    public required DateTime SubmittedAt { get; init; }

    /// <summary>When it was last submitted or resubmitted (equals <see cref="SubmittedAt"/> until a resubmission).</summary>
    public required DateTime LastSubmittedAt { get; init; }

    /// <summary>The Safeguarding Officer who made the last stage-1 decision (approve, ask for more information, reject).</summary>
    public int? SafeguardingReviewedByUserId { get; init; }
    public DateTime? SafeguardingReviewedAt { get; init; }

    /// <summary>The Admin who made the stage-2 decision. May be the same person as the stage-1 reviewer (allowed, adr/0008 answer 7).</summary>
    public int? AdminDecidedByUserId { get; init; }
    public DateTime? AdminDecidedAt { get; init; }

    /// <summary>The <see cref="Student"/> created, or linked to, on approval. Null until approved.</summary>
    public int? StudentId { get; init; }

    /// <summary>True when the Admin linked this parent to an existing student (a second parent of the same child) instead of creating one.</summary>
    public bool LinkedToExistingStudent { get; init; }

    /// <summary>The approved <see cref="ConsentRecord"/> created from this application on approval.</summary>
    public int? ConsentRecordId { get; init; }

    public AppUser? ApplicantUser { get; init; }
    public AppUser? SafeguardingReviewedByUser { get; init; }
    public AppUser? AdminDecidedByUser { get; init; }
    public Student? Student { get; init; }
    public ConsentRecord? ConsentRecord { get; init; }
    public StudentApplicationSensitiveDetails? SensitiveDetails { get; init; }
}
