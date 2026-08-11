namespace Vlms.Domain;

public enum StudentStatus
{
    Active,
    Inactive,
    Graduated
}

public sealed class Student
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required DateOnly DateOfBirth { get; init; }
    public required int CurrentRankId { get; init; }
    public required StudentStatus Status { get; init; }
    public required DateOnly EnrolmentDate { get; init; }

    /// <summary>
    /// STATE.md "Manual 'leaving' flag..." — when this Student was marked <see cref="StudentStatus.Inactive"/>
    /// via the Admin-only <c>Retention.LeavingService.MarkStudentInactiveAsync</c>, feeding
    /// <c>Reporting.RetentionFlagging</c>'s 3-years-from-leaving retention check
    /// (docs/governance/security-compliance.md). Captured automatically as the action's own
    /// timestamp, not manually entered/backdated by the Admin — a deliberate build-time reading of
    /// "a boolean the Admin explicitly enables" (see <c>LeavingService</c>'s doc comment). Null for
    /// every Student except one marked Inactive by that service; not set for a Graduated student
    /// (their retention anchor is derived from <c>StudentRankProgress.CompletedAt</c> instead — see
    /// <c>RetentionFlagging</c>'s doc comment for why the two entity states use different anchors).
    /// </summary>
    public DateTime? LeftAt { get; init; }

    /// <summary>Confirmed: Teachers see all students, so this does not scope visibility — it is informational.</summary>
    public int? AssignedTeacherUserId { get; init; }

    /// <summary>
    /// The AppUser this Student is the self-login identity for (added during implementation to
    /// support self/parent login — see STATE.md and data-design.md). Null until the student has
    /// an Entra External ID sign-in provisioned; drives the Student resource-based authorization
    /// check (a Student may only act on their own record).
    /// </summary>
    public int? AppUserId { get; init; }

    public Rank? CurrentRank { get; init; }
    public AppUser? AssignedTeacherUser { get; init; }
    public AppUser? AppUser { get; init; }
    public ICollection<StudentLessonCompletion> Completions { get; init; } = new List<StudentLessonCompletion>();
    public ICollection<StudentRankProgress> RankProgressHistory { get; init; } = new List<StudentRankProgress>();
    public ICollection<StudentBadge> Badges { get; init; } = new List<StudentBadge>();
    public ICollection<ConsentRecord> ConsentRecords { get; init; } = new List<ConsentRecord>();
    public ICollection<StudentGuardianLink> GuardianLinks { get; init; } = new List<StudentGuardianLink>();
}
