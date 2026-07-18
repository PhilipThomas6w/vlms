namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// The shape of <see cref="Vlms.Domain.LessonChangeProposal.ProposedContent"/> once parsed —
/// serialized to/from JSON (<see cref="System.Text.Json.JsonSerializer"/>) by
/// <see cref="LessonProposalService"/>. Not specified by docs/design/data-design.md beyond
/// "ProposedContent" being a string, so this is a build-time decision: a proposal always carries
/// the *full* target state of the <see cref="Vlms.Domain.Lesson"/> (not a partial patch) — Create,
/// Edit, and Retire are all "apply this content" with different starting points, which keeps
/// <see cref="LessonProposalService"/>'s apply logic uniform. A Retire proposal is expected to
/// carry the lesson's existing Code/Title/ContentBlobKey with <see cref="IsActive"/> set to false.
/// </summary>
public sealed class ProposedLessonContent
{
    /// <summary>Required when the proposal creates a brand-new Lesson (ChangeType == Create); ignored otherwise.</summary>
    public int? RankId { get; init; }

    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string ContentBlobKey { get; init; }
    public required bool IsActive { get; init; }
}
