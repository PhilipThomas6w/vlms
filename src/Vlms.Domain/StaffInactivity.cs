namespace Vlms.Domain;

/// <summary>
/// One period of a Teacher, Admin or Safeguarding Officer being Inactive in one chapter and, once
/// reactivated, returning (functional.md FR-017 as amended 27/10/2026). Written by
/// <c>Retention.LeavingService</c> only: a row is opened when they are marked Inactive and closed
/// (<see cref="ReactivatedAt"/>, <see cref="ReactivatedByUserId"/>) when an Admin reactivates them.
/// At most one row per person and chapter is open; an open row is the only record that they are
/// Inactive there, and its <see cref="InactiveAt"/> starts the retention clock.
///
/// National table, like <see cref="UserRole"/>: <see cref="ChapterId"/> records the chapter whose
/// Admin acted (the chapter holding their staff roles) and the service reads rows of the active
/// chapter only; it carries no chapter query filter.
/// </summary>
public sealed class StaffInactivity
{
    public int Id { get; init; }

    /// <summary>The person who was marked Inactive. Their rows go with their account.</summary>
    public required int UserId { get; init; }

    public required int ChapterId { get; init; }

    public required DateTime InactiveAt { get; init; }

    /// <summary>
    /// The Admin who recorded it; null for an Inactive record recorded before this table existed
    /// (<see cref="RecordedBeforeHistory"/>), or once the Admin's account has been deleted under
    /// the retention rules (docs/adr/0014).
    /// </summary>
    public int? MarkedInactiveByUserId { get; init; }

    /// <summary>
    /// True for an Inactive record copied in when this table was created, which names no Admin. Tells it
    /// apart from one whose Admin's account was later deleted, which shows as "a former member of staff".
    /// </summary>
    public bool RecordedBeforeHistory { get; init; }

    public DateTime? ReactivatedAt { get; init; }

    /// <summary>The Admin who reactivated them; null once that Admin's account has been deleted (docs/adr/0014).</summary>
    public int? ReactivatedByUserId { get; init; }
}
