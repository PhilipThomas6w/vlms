namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// The church consent year (adr/0008 answer 2, FR-001): 1 September to 31 August. Consent always
/// ends on the 31 August that closes the church year containing the day it is submitted, with no
/// cut-off: submitted 1 July 2027 expires 31 August 2027, submitted 1 September 2027 expires
/// 31 August 2028.
///
/// <b>Clock.</b> <see cref="Today"/> is the UTC calendar date, the same "today" every other consent
/// check in the codebase uses (<c>CompletionService</c>, <c>ConsentExpiryJob</c>,
/// <c>ParentConsentService</c>), so the expiry written at submission and the expiry compared at
/// completion agree. This differs from the UK date only between 00:00 and 01:00 during British
/// Summer Time, and a one-hour edge on 31 August / 1 September is accepted rather than make one
/// consent path disagree with the others.
/// </summary>
public static class ConsentYear
{
    /// <summary>The 31 August that ends the church year containing <paramref name="date"/>.</summary>
    public static DateOnly EndFor(DateOnly date) =>
        new(date.Month >= 9 ? date.Year + 1 : date.Year, 8, 31);

    /// <summary>Today's date on the clock the consent checks use (UTC calendar date).</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
}
