using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// The shape of <see cref="Vlms.Domain.LessonChangeProposal.ProposedContent"/> once parsed —
/// serialized to/from JSON (<see cref="System.Text.Json.JsonSerializer"/>) by
/// <see cref="LessonProposalService"/>. Not specified by docs/design/data-design.md beyond
/// "ProposedContent" being a string, so this is a build-time decision: a proposal always carries
/// the *full* target state of the <see cref="Vlms.Domain.Lesson"/> (not a partial patch) — Create,
/// Edit, and Retire are all "apply this content" with different starting points, which keeps
/// <see cref="LessonProposalService"/>'s apply logic uniform. A Retire proposal is expected to
/// carry the lesson's existing Code/Title/Order/Tags/Files with <see cref="IsActive"/> set to false.
/// Tags and Files replace the lesson's current sets wholesale on approval.
/// </summary>
public sealed class ProposedLessonContent
{
    /// <summary>Required when the proposal creates a brand-new Lesson (ChangeType == Create); ignored otherwise.</summary>
    public int? RankId { get; init; }

    public required string Code { get; init; }
    public required string Title { get; init; }
    public required int Order { get; init; }
    public required IReadOnlyList<LessonTag> Tags { get; init; }
    public required IReadOnlyList<ProposedLessonFile> Files { get; init; }
    public required bool IsActive { get; init; }
}

/// <summary>One file of a <see cref="ProposedLessonContent"/>, in display order.</summary>
public sealed class ProposedLessonFile
{
    public required string FileName { get; init; }
    public required string BlobKey { get; init; }
}
