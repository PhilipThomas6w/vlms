using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

public enum ReviewFileChange
{
    Unchanged,
    Added,
    Removed
}

/// <summary>One compared attribute of a lesson. <see cref="Current"/> is null for a new lesson.</summary>
public sealed record ReviewField(string Label, string? Current, string Proposed, bool Changed);

/// <summary>A file in the review, with the URL that opens it (live library or pending-file endpoint).</summary>
public sealed record ReviewFile(string FileName, string Url, ReviewFileChange Change);

/// <summary>
/// A proposal laid out against the current lesson for the Approver (FR-007), built from the proposal's
/// stored content. Current files link to the library endpoint; proposed files link to the
/// proposer/Approver-only proposal-file endpoint.
/// </summary>
public sealed record ProposalReview(
    int ProposalId,
    LessonChangeType ChangeType,
    string ProposedBy,
    DateTime SubmittedAt,
    bool HasCurrent,
    IReadOnlyList<ReviewField> Fields,
    IReadOnlyList<ReviewFile> CurrentFiles,
    IReadOnlyList<ReviewFile> ProposedFiles,
    bool FilesChanged,
    bool FileOrderChanged)
{
    public static string ProposalFileUrl(int proposalId, int index) => $"/curriculum/proposals/{proposalId}/files/{index}";

    public static ProposalReview Build(LessonChangeProposal proposal, ProposedLessonContent proposed, Lesson? current, string rankName)
    {
        var currentTags = current is null
            ? null
            : string.Join(", ", current.Tags.Select(t => t.Tag).Order().Select(LessonTagNames.Display));
        var proposedTags = string.Join(", ", proposed.Tags.Distinct().Order().Select(LessonTagNames.Display));

        ReviewField Field(string label, string? now, string next) =>
            new(label, now, next, current is null || now != next);

        var fields = new List<ReviewField>
        {
            new("Rank", current is null ? null : rankName, rankName, current is null),
            Field("Code", current?.Code, proposed.Code),
            Field("Title", current?.Title, proposed.Title),
            Field("Delivery order", current?.Order.ToString(), proposed.Order.ToString()),
            Field("Status", current is null ? null : (current.IsActive ? "Active" : "Retired"), proposed.IsActive ? "Active" : "Retired"),
            Field("Tags", currentTags, proposedTags)
        };

        var currentFiles = current?.Files.OrderBy(f => f.Order).ThenBy(f => f.Id).ToList() ?? [];
        var currentKeys = currentFiles.Select(f => f.BlobKey).ToHashSet();
        var proposedKeys = proposed.Files.Select(f => f.BlobKey).ToHashSet();

        var proposedRows = proposed.Files
            .Select((f, i) => new ReviewFile(
                f.FileName,
                ProposalFileUrl(proposal.Id, i),
                currentKeys.Contains(f.BlobKey) ? ReviewFileChange.Unchanged : ReviewFileChange.Added))
            .ToList();
        var currentRows = currentFiles
            .Select(f => new ReviewFile(
                f.FileName,
                $"/lessons/files/{f.Id}",
                proposedKeys.Contains(f.BlobKey) ? ReviewFileChange.Unchanged : ReviewFileChange.Removed))
            .ToList();

        var keptCurrentOrder = currentFiles.Where(f => proposedKeys.Contains(f.BlobKey)).Select(f => f.BlobKey);
        var keptProposedOrder = proposed.Files.Where(f => currentKeys.Contains(f.BlobKey)).Select(f => f.BlobKey);

        return new ProposalReview(
            proposal.Id,
            proposal.ChangeType,
            proposal.ProposedByUser?.DisplayName ?? "",
            proposal.SubmittedAt,
            current is not null,
            fields,
            currentRows,
            proposedRows,
            FilesChanged: proposedRows.Any(f => f.Change != ReviewFileChange.Unchanged)
                          || currentRows.Any(f => f.Change != ReviewFileChange.Unchanged),
            FileOrderChanged: !keptCurrentOrder.SequenceEqual(keptProposedOrder));
    }
}
