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
