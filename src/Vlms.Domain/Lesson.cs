namespace Vlms.Domain;

/// <summary>Current live version of a lesson's content within a <see cref="Rank"/>.</summary>
public sealed class Lesson
{
    public required int Id { get; init; }
    public required int RankId { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string ContentBlobKey { get; init; }
    public required bool IsActive { get; init; }

    public Rank? Rank { get; init; }
    public ICollection<LessonChangeProposal> ChangeProposals { get; init; } = new List<LessonChangeProposal>();
    public ICollection<StudentLessonCompletion> Completions { get; init; } = new List<StudentLessonCompletion>();
}
