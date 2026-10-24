namespace Vlms.Domain;

public enum DbsCheckStatus
{
    Pending,
    Clear,
    Flagged
}

/// <summary>
/// DBS (Disclosure and Barring Service) check, tracked against Teachers. Access restricted
/// entirely to Safeguarding Officer and Admin via the sensitive-data query filter — Teacher and
/// Approver have no access at all (whole entity, not column-level).
/// </summary>
public sealed class DbsCheck : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md). Left at 0, it is set on save to the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int TeacherUserId { get; init; }
    public required DateOnly CheckDate { get; init; }
    public required DateOnly ExpiryDate { get; init; }
    public required string CertificateNumber { get; init; }
    public required DbsCheckStatus Status { get; init; }

    public AppUser? TeacherUser { get; init; }
}
