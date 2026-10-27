namespace Vlms.Domain;

/// <summary>
/// One period of a Teacher, Admin or Safeguarding Officer stepping down and, once reinstated,
/// returning (functional.md FR-017 as amended 27/10/2026). Written by
/// <c>Retention.LeavingService</c> only: a row is opened when they step down and closed
/// (<see cref="ReinstatedAt"/>, <see cref="ReinstatedByUserId"/>) when an Admin reinstates them.
/// At most one row per person is open, and it is open exactly while <see cref="AppUser.HasLeft"/>
/// is true. <see cref="AppUser.LeftAt"/> stays the current step-down date, which the retention
/// rules read.
///
/// National table, like <see cref="UserRole"/>: <see cref="ChapterId"/> records the chapter whose
/// Admin acted (the chapter holding their staff roles) and the service reads rows of the active
/// chapter only; it carries no chapter query filter.
/// </summary>
public sealed class StaffStepDown
{
    public int Id { get; init; }

    /// <summary>The person who stepped down. Their rows go with their account.</summary>
    public required int UserId { get; init; }

    public required int ChapterId { get; init; }

    public required DateTime SteppedDownAt { get; init; }

    /// <summary>The Admin who recorded it; null only for a step-down recorded before this table existed.</summary>
    public int? SteppedDownByUserId { get; init; }

    public DateTime? ReinstatedAt { get; init; }

    public int? ReinstatedByUserId { get; init; }
}
