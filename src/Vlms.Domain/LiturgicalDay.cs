namespace Vlms.Domain;

/// <summary>
/// One day of the Church's liturgical calendar (functional.md FR-022), imported from the New Calendar
/// feed published by the Greek Orthodox Archdiocese of America: the day's main feast, its fasting
/// status, its saints and its readings. National, the same for every chapter; written only by the
/// scheduled import.
/// </summary>
public sealed class LiturgicalDay
{
    public required DateOnly Date { get; init; }
    public required string Title { get; set; }
    public required LiturgicalFast Fast { get; set; }

    /// <summary>The saints and feasts of the day, as the feed lists them, separated by "; ".</summary>
    public required string Saints { get; set; }

    public ICollection<LiturgicalReading> Readings { get; init; } = new List<LiturgicalReading>();
}

/// <summary>A day's fasting status as the feed states it. <see cref="None"/>: the feed states none.</summary>
public enum LiturgicalFast
{
    None = 0,
    FastFree = 1,
    WineAndOil = 2,
    Fish = 3,
    DairyEggsAndFish = 4,
    Strict = 5,
}

/// <summary>One reading of a <see cref="LiturgicalDay"/>, for example "Epistle Reading", "Galatians 2:16-20", and its text.</summary>
public sealed class LiturgicalReading
{
    public required int Id { get; init; }
    public required DateOnly Date { get; init; }
    public required int Order { get; init; }
    public required string Heading { get; init; }
    public required string Reference { get; init; }
    public required string Text { get; init; }
}
