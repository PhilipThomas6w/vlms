namespace Vlms.Domain;

/// <summary>
/// A British (including Welsh) or Irish saint added to the saints of a day, every year (functional.md
/// FR-022; Philip, 25/10/2026). National, the same in every chapter; added only by a National
/// Administrator, one by one or imported from a .csv file. Shown after the saints of the imported
/// liturgical calendar, and hidden on a day where that calendar already lists the same name. Adds to
/// the saints only, never the feast, fast or readings. One saint may be added on several dates.
/// </summary>
public sealed class AddedSaint
{
    public required int Id { get; init; }

    /// <summary>The month (1-12) of the date the saint is kept on each year.</summary>
    public required int Month { get; init; }

    /// <summary>The day of the month (1-31) the saint is kept on each year; 29 February shows only in leap years.</summary>
    public required int Day { get; init; }

    public required string Name { get; init; }
    /// <summary>Who added it; null once that person's record has been removed.</summary>
    public int? AddedByUserId { get; init; }
    public required DateTime AddedAt { get; init; }
}
