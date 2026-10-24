namespace Vlms.Domain;

/// <summary>
/// A local chapter of Varangians at one Orthodox parish (functional.md FR-021,
/// docs/adr/0010-chapters.md). Every person-level record (students and everything hanging off them,
/// guardians, consent, DBS checks, registration applications) belongs to exactly one chapter
/// (<see cref="IChapterOwned"/>); the curriculum, ranks and rank badges are national. Created and
/// edited only by a <see cref="Role.NationalAdministrator"/>.
/// </summary>
public sealed class Chapter
{
    /// <summary>Shrewsbury, the first chapter: every record that existed before chapters belongs to it.</summary>
    public const int ShrewsburyId = 1;

    public const int MaxNameLength = 200;

    public required int Id { get; init; }

    /// <summary>The chapter's name as shown to its signed-in members, usually the parish's name.</summary>
    public required string Name { get; init; }

    /// <summary>The town or place of the parish, e.g. "Shrewsbury".</summary>
    public required string Place { get; init; }

    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Whether the chapter is open. An inactive chapter is not offered to parents applying to
    /// register a child; its existing records and roles are untouched.
    /// </summary>
    public required bool IsActive { get; init; }
}
