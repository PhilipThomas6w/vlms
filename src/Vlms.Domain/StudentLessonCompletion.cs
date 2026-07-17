namespace Vlms.Domain;

/// <summary>Teacher marks and can self-correct (<see cref="IsReversed"/>/<see cref="ReversedAt"/>) — no separate Admin correction gate.</summary>
public sealed class StudentLessonCompletion
{
    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required int LessonId { get; init; }
    public required int CompletedByUserId { get; init; }
    public required DateTime CompletedAt { get; init; }
    public string? Note { get; init; }
    public required bool IsReversed { get; init; }
    public DateTime? ReversedAt { get; init; }

    public Student? Student { get; init; }
    public Lesson? Lesson { get; init; }
    public AppUser? CompletedByUser { get; init; }
    public Certificate? Certificate { get; init; }
}
