namespace Vlms.Domain;

/// <summary>
/// The fixed topic tags a lesson can carry, taken from the programme syllabus's strand headings
/// (docs/requirements/functional.md "Curriculum management"; Philip's decision, 21/10/2026).
/// Stored as its integer value, so append new members rather than reordering.
/// </summary>
public enum LessonTag
{
    ChurchLife,
    Worship,
    ChurchInterior,
    Mysteries,
    Prayers,
    Saints,
    ChurchHistory,
    OldTestament,
    NewTestament,
    ChurchYear,
    ChristianLife
}
