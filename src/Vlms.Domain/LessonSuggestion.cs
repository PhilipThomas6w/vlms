namespace Vlms.Domain;

/// <summary>Where a <see cref="LessonSuggestion"/> is (docs/adr/0013 decisions 1 to 3). Stored as its integer value.</summary>
public enum LessonSuggestionStatus
{
    /// <summary>Being written; only its author sees it.</summary>
    Draft,

    /// <summary>Awaiting an Approver's decision.</summary>
    Submitted,

    /// <summary>Approved or published directly: it is now a <see cref="LessonVersion"/>.</summary>
    Approved,

    /// <summary>Sent back with a comment; kept with it, and its author can carry it on as a new draft.</summary>
    SentBack
}

/// <summary>What a <see cref="LessonSuggestion"/> changes (docs/adr/0013 decisions 5 and 10). Stored as its integer value.</summary>
public enum LessonChangeKind
{
    Edit,
    ReplaceInFull,
    NewLesson,
    Retire
}

/// <summary>
/// A suggested change to a lesson (FR-007, docs/adr/0013): its full proposed text, title, order, tags and pictures, the
/// version it was based on, and its reason. Only one suggestion per lesson may be open (a draft or submitted) at a time
/// (decision 2), enforced by <c>LessonSuggestionService</c> and a unique filtered index.
/// </summary>
public sealed class LessonSuggestion
{
    public required int Id { get; init; }

    /// <summary>The lesson changed; null for a new lesson until it is approved.</summary>
    public int? LessonId { get; init; }

    /// <summary>The rank of the lesson (the new lesson's rank, for <see cref="LessonChangeKind.NewLesson"/>).</summary>
    public required int RankId { get; init; }

    public required LessonChangeKind Kind { get; init; }
    public required LessonSuggestionStatus Status { get; init; }

    /// <summary>The <see cref="LessonVersion.Number"/> it was based on; null for a new lesson. A suggestion based on an older version cannot be approved.</summary>
    public int? BaseVersionNumber { get; init; }

    public required int AuthorUserId { get; init; }

    public required string Title { get; init; }
    public required int Order { get; init; }
    public required string Body { get; init; }
    public string? Reason { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public DateTime? SubmittedAt { get; init; }

    /// <summary>When it was approved, published directly or sent back.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>The Approver who approved, published or sent it back.</summary>
    public int? DecidedByUserId { get; init; }

    public string? SentBackComment { get; init; }

    /// <summary>The sent-back suggestion this draft carries on, if any.</summary>
    public int? RevisesSuggestionId { get; init; }

    public Lesson? Lesson { get; init; }
    public AppUser? AuthorUser { get; init; }
    public AppUser? DecidedByUser { get; init; }
    public ICollection<LessonSuggestionTag> Tags { get; init; } = new List<LessonSuggestionTag>();
    public ICollection<LessonSuggestionPicture> Pictures { get; init; } = new List<LessonSuggestionPicture>();
}

/// <summary>A tag of a <see cref="LessonSuggestion"/>, one row per tag.</summary>
public sealed class LessonSuggestionTag
{
    public required int LessonSuggestionId { get; init; }
    public required LessonTag Tag { get; init; }
}

/// <summary>
/// A picture a <see cref="LessonSuggestion"/>'s text may use, by bare file name: the lesson's own pictures when the draft
/// was started, and any inserted in the editor (resized on upload). Readable only by its author and Approvers.
/// </summary>
public sealed class LessonSuggestionPicture
{
    public required int Id { get; init; }
    public required int LessonSuggestionId { get; init; }
    public required string FileName { get; init; }
    public required string BlobKey { get; init; }

    public LessonSuggestion? LessonSuggestion { get; init; }
}

/// <summary>
/// One change of the national setting "Direct publishing allowed" / "Review required" (docs/adr/0013 decision 10): the
/// value set, by whom and when. The latest row is the setting; with none, direct publishing is allowed (the setting for
/// the first pass). Rows are never changed or removed, so they are the setting's audit.
/// </summary>
public sealed class LessonPublishingSettingChange
{
    public required int Id { get; init; }
    public required bool DirectPublishingAllowed { get; init; }
    public required int ChangedByUserId { get; init; }
    public required DateTime ChangedAt { get; init; }

    public AppUser? ChangedByUser { get; init; }
}
