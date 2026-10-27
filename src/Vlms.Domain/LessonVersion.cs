namespace Vlms.Domain;

/// <summary>
/// How a <see cref="LessonVersion"/> was published (docs/adr/0013-lesson-suggestions-review-and-history.md decisions 4
/// and 10). Stored as its integer value, so append new members rather than reordering.
/// </summary>
public enum LessonVersionKind
{
    /// <summary>Written by the curriculum seeder from a converted lesson (decision 9).</summary>
    Converted,

    /// <summary>A suggestion an Approver approved (decision 3).</summary>
    ApprovedSuggestion,

    /// <summary>Published at once by an Approver with "Publish now", while direct publishing is allowed (decision 10).</summary>
    PublishedDirectly,

    /// <summary>The whole lesson replaced from a blank page or pasted text, approved or published directly (decision 10).</summary>
    ReplacedInFull
}

/// <summary>
/// One published version of a lesson, kept permanently (docs/adr/0013 decision 4; FR-007): numbered from 1 per lesson,
/// holding the lesson as it then was (text, title, order, tags, pictures, whether active) and who suggested, approved or
/// published it. Never changed or removed once written: the version history cannot be switched off (decision 10).
/// </summary>
public sealed class LessonVersion
{
    public required int Id { get; init; }
    public required int LessonId { get; init; }

    /// <summary>1, 2, 3... per lesson.</summary>
    public required int Number { get; init; }

    public required LessonVersionKind Kind { get; init; }

    public required string Title { get; init; }
    public required int Order { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>The lesson text (Markdown); null for a lesson that had not been converted (files only).</summary>
    public string? Body { get; init; }

    /// <summary>The lesson's <see cref="Lesson.ContentVersion"/> once this version was live.</summary>
    public string? ContentVersion { get; init; }

    /// <summary>Who suggested the change; null for a converted version.</summary>
    public int? SuggestedByUserId { get; init; }

    public DateTime? SuggestedAt { get; init; }

    /// <summary>The Approver who approved the suggestion; null when published directly or converted.</summary>
    public int? ApprovedByUserId { get; init; }

    /// <summary>The Approver who published it directly with "Publish now"; null otherwise.</summary>
    public int? PublishedByUserId { get; init; }

    /// <summary>
    /// True when it was published directly with "Publish now" rather than approved. The approving
    /// or publishing Approver's id is cleared when their account is deleted under the retention
    /// rules (docs/adr/0014); this keeps which of the two it was, so the history still says
    /// "Approved by" or "Published directly by" a former member of staff.
    /// </summary>
    public bool PublishedDirectly { get; init; }

    public string? Reason { get; init; }

    /// <summary>When it became the live lesson.</summary>
    public required DateTime PublishedAt { get; init; }

    /// <summary>The suggestion it came from; null for a converted version.</summary>
    public int? SuggestionId { get; init; }

    public Lesson? Lesson { get; init; }
    public LessonSuggestion? Suggestion { get; init; }
    public ICollection<LessonVersionTag> Tags { get; init; } = new List<LessonVersionTag>();
    public ICollection<LessonVersionPicture> Pictures { get; init; } = new List<LessonVersionPicture>();
}

/// <summary>A tag of a <see cref="LessonVersion"/>, one row per tag.</summary>
public sealed class LessonVersionTag
{
    public required int LessonVersionId { get; init; }
    public required LessonTag Tag { get; init; }
}

/// <summary>
/// A picture of a <see cref="LessonVersion"/>: the file name its text uses and the stored (resized) copy. Blob keys of
/// lesson pictures are never overwritten with different content, so every version's pictures stay retrievable.
/// </summary>
public sealed class LessonVersionPicture
{
    public required int Id { get; init; }
    public required int LessonVersionId { get; init; }
    public required int Order { get; init; }
    public required string FileName { get; init; }
    public required string BlobKey { get; init; }

    public LessonVersion? LessonVersion { get; init; }
}
