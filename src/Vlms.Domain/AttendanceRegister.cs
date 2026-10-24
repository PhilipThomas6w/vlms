namespace Vlms.Domain;

/// <summary>
/// The register a chapter's teacher takes at one meeting (functional.md FR-020): one per chapter per
/// meeting date, with a present or absent mark for each student on roll. Self-contained in its
/// chapter (docs/design/chapters-and-attendance.md section 5): only that chapter's staff take and read it.
/// </summary>
public sealed class AttendanceRegister : IChapterOwned
{
    /// <summary>The chapter this register belongs to (docs/adr/0010-chapters.md). Left at 0, it is the caller's active chapter.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required DateOnly MeetingDate { get; init; }
    public required int TakenByUserId { get; set; }
    public required DateTime TakenAt { get; set; }

    public AppUser? TakenByUser { get; init; }
    public ICollection<AttendanceMark> Marks { get; init; } = new List<AttendanceMark>();
}
