namespace Vlms.Domain;

/// <summary>
/// One student's mark on one <see cref="AttendanceRegister"/>: present or absent only (Philip,
/// 24/10/2026). Carries the register's <see cref="MeetingDate"/> so a parent reading their own child's
/// attendance never needs the register itself. Deleted with the student (retention: part of the
/// student's record).
/// </summary>
public sealed class AttendanceMark : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its register's and its student's. Left at 0, it is copied on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int AttendanceRegisterId { get; init; }
    public required int StudentId { get; init; }
    public required DateOnly MeetingDate { get; init; }
    public required bool Present { get; set; }

    /// <summary>
    /// Whether the mark is from a class (true) rather than an excursion, copied from the register's
    /// event when the mark is made. Only class marks count towards attendance figures and the at-risk
    /// rule (functional.md FR-020 as corrected 25/10/2026).
    /// </summary>
    public bool ClassSession { get; init; } = true;

    public AttendanceRegister? AttendanceRegister { get; init; }
    public Student? Student { get; init; }
}
