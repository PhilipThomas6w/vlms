using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Lesson suggestions, review and history (FR-007, docs/adr/0013-lesson-suggestions-review-and-history.md), replacing the
/// file-upload proposal workflow:
/// <list type="bullet">
/// <item>Teachers (and Approvers) start a suggestion from a lesson, save it as a draft and submit it with a reason
/// (decision 1); a new lesson and a retirement are suggestions too, and a whole lesson can be replaced (decisions 5, 10).</item>
/// <item>Only one suggestion per lesson is open (a draft or submitted) at a time (decision 2): checked here, and by a
/// unique filtered index on <c>lesson_suggestions</c>.</item>
/// <item>Approvers approve or send back with a comment; one is enough, and they may approve their own (decision 3). They
/// may "Publish now" while the national setting allows direct publishing (decision 10), which only a National
/// Administrator changes, each change recorded.</item>
/// <item>Approving or publishing adds the lesson's next <see cref="LessonVersion"/>, kept permanently, updates the live
/// lesson and changes its <see cref="Lesson.ContentVersion"/>, so copies saved on devices refresh (decision 4; FR-023).
/// A suggestion based on an older version than the lesson's current one can never be approved or published.</item>
/// <item>Any Approver may instead refer a submitted suggestion to committee (decision 8): it is then published once
/// <see cref="LessonSuggestionApproval.Required"/> different Approvers have approved it, the author's own approval
/// counting, each Approver at most once; any Approver may send it back with a comment, which ends it. Every Approver is
/// emailed when a suggestion is submitted (<see cref="LessonSuggestionNotificationService"/>), and the menu shows how
/// many await the caller (<see cref="CountAwaitingMyReviewAsync"/>). Someone who is not an Approver can only suggest:
/// "Publish now", approving and referring all require the Approver role.</item>
/// <item>Approvers and Admins read the history, which records each referral, committee approval and outcome.</item>
/// </list>
///
/// <b>Authorization</b> is checked here on every call, not only by the pages' policies: the same defence in depth as the
/// rest of the curriculum (<see cref="LessonLibraryService"/>). The Approver role is curriculum only: nothing here reads or
/// writes <c>DbsCheck</c> or <c>ConsentSensitiveDetails</c>, and lessons, versions and suggestions are national, carrying no
/// chapter or sensitive-data query filter (as <see cref="LessonLibraryService"/> treats lessons).
///
/// Ids are application-assigned (<see cref="VlmsDbContext.NextIdAsync{T}"/>, max + 1), as elsewhere: race-prone under
/// concurrent writers, acceptable at this system's tens-of-users scale (VISION.md).
/// </summary>
public sealed class LessonSuggestionService
{
    /// <summary>Who may suggest changes: Teachers, and Approvers (whose editor also offers "Publish now").</summary>
    public static readonly IReadOnlyList<Role> SuggesterRoles = [Role.Teacher, Role.Approver];

    /// <summary>Who may read a lesson's history (decision 4): Approvers and Admins.</summary>
    public static readonly IReadOnlyList<Role> HistoryRoles = [Role.Approver, Role.Admin];

    /// <summary>Largest picture accepted from the editor, before resizing.</summary>
    public const int MaxPictureBytes = 20 * 1024 * 1024;

    public const int MaxTitleLength = 200;
    public const int MaxReasonLength = 2000;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly IBlobStorage _blobs;
    private readonly LessonSuggestionNotificationService? _notifications;

    public LessonSuggestionService(VlmsDbContext db, ICurrentUserContext currentUser, IBlobStorage blobs,
        LessonSuggestionNotificationService? notifications = null)
    {
        _db = db;
        _currentUser = currentUser;
        _blobs = blobs;
        _notifications = notifications;
    }

    /// <summary>Where a suggestion's picture is served (author and Approvers only).</summary>
    public static string SuggestionPictureUrl(int suggestionId, string fileName) =>
        $"/curriculum/suggestions/{suggestionId}/pictures/{Uri.EscapeDataString(fileName)}";

    /// <summary>Where a version's picture is served (Approvers and Admins only).</summary>
    public static string VersionPictureUrl(int pictureId) => $"/lessons/versions/pictures/{pictureId}";

    public static string EditorUrl(string lessonCode) => $"/lessons/{Uri.EscapeDataString(lessonCode)}/suggest";
    public static string NewLessonUrl(string rankCode) => $"/lessons/new/{Uri.EscapeDataString(rankCode)}";
    public static string HistoryUrl(string lessonCode) => $"/lessons/{Uri.EscapeDataString(lessonCode)}/history";
    public static string ReviewUrl(int suggestionId) => $"/curriculum/review/{suggestionId}";

    public bool CanSuggest => SuggesterRoles.Any(_currentUser.HasRole);
    public bool CanReadHistory => HistoryRoles.Any(_currentUser.HasRole);

    // --- The lesson page -------------------------------------------------------------------------------------------

    /// <summary>
    /// What the lesson page shows about suggestions: the current version number for everyone, and for those who may
    /// suggest, whether a suggestion is open and whose. Students and parents see only the published lesson.
    /// </summary>
    public async Task<LessonPageSuggestions> GetLessonPageAsync(int lessonId, CancellationToken ct = default)
    {
        var version = await CurrentVersionNumberAsync(lessonId, ct);
        if (!CanSuggest)
        {
            return new LessonPageSuggestions(version, false, CanReadHistory, null);
        }

        var open = await OpenSuggestionAsync(lessonId, ct);
        return new LessonPageSuggestions(version, true, CanReadHistory, open);
    }

    // --- The editor ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The editor for a lesson: the caller's own open suggestion if they have one, otherwise a new one starting from the
    /// lesson as it is (or from their latest suggestion that was sent back, still based on the current version). When
    /// someone else's suggestion is open, <see cref="SuggestionEditor.BlockedBy"/> says so and there is nothing to edit.
    /// Null for an unknown, retired or unconverted lesson.
    /// </summary>
    public async Task<SuggestionEditor?> GetEditorAsync(string lessonCode, CancellationToken ct = default)
    {
        RequireSuggester();
        var userId = RequireUserId();

        var lesson = await _db.Lessons.AsNoTracking().Include(l => l.Rank).Include(l => l.Tags).Include(l => l.Pictures)
            .SingleOrDefaultAsync(l => l.Code == lessonCode && l.IsActive && l.Body != null, ct);
        if (lesson is null)
        {
            return null;
        }

        var version = await CurrentVersionNumberAsync(lesson.Id, ct);
        var canPublish = await CanPublishNowAsync(ct);
        var open = await OpenSuggestionAsync(lesson.Id, ct);
        if (open is not null && !open.IsMine)
        {
            return new SuggestionEditor(lesson.Id, lesson.Code, lesson.Title, lesson.RankId, lesson.Rank!.Name, version, open, null, canPublish, null);
        }

        if (open is not null)
        {
            var mine = await LoadSuggestionAsync(open.Id, ct);
            return new SuggestionEditor(lesson.Id, lesson.Code, lesson.Title, lesson.RankId, lesson.Rank!.Name, version, null,
                ToDraftView(mine), canPublish, await SentBackInfoAsync(mine.RevisesSuggestionId, ct));
        }

        // A suggestion of the caller's that was sent back, based on the current version, carries on as their new draft.
        var sentBack = await _db.LessonSuggestions.AsNoTracking().Include(s => s.Tags).Include(s => s.Pictures)
            .Where(s => s.LessonId == lesson.Id && s.AuthorUserId == userId && s.Status == LessonSuggestionStatus.SentBack
                && s.BaseVersionNumber == version)
            .OrderByDescending(s => s.DecidedAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (sentBack is not null
            && !await _db.LessonSuggestions.AnyAsync(s => s.RevisesSuggestionId == sentBack.Id, ct))
        {
            var view = ToDraftView(sentBack) with { Id = null, Status = null, RevisesSuggestionId = sentBack.Id };
            return new SuggestionEditor(lesson.Id, lesson.Code, lesson.Title, lesson.RankId, lesson.Rank!.Name, version, null,
                view, canPublish, await SentBackInfoAsync(sentBack.Id, ct));
        }

        var fresh = new SuggestionDraftView(null, null, LessonChangeKind.Edit, lesson.Title, lesson.Order,
            lesson.Tags.Select(t => t.Tag).Order().ToList(), lesson.Body!, null,
            lesson.Pictures.ToDictionary(p => p.FileName, p => LessonLibraryService.PictureUrl(p.Id), StringComparer.Ordinal), null);
        return new SuggestionEditor(lesson.Id, lesson.Code, lesson.Title, lesson.RankId, lesson.Rank!.Name, version, null, fresh, canPublish, null);
    }

    /// <summary>
    /// The editor for a new lesson in a rank (decision 5): the caller's own open new-lesson draft in that rank, or a blank
    /// one. Null for an unknown rank.
    /// </summary>
    public async Task<SuggestionEditor?> GetNewLessonEditorAsync(string rankCode, int? suggestionId = null, CancellationToken ct = default)
    {
        RequireSuggester();
        var userId = RequireUserId();

        var rank = await _db.Ranks.AsNoTracking().SingleOrDefaultAsync(r => r.Code == rankCode, ct);
        if (rank is null)
        {
            return null;
        }

        var canPublish = await CanPublishNowAsync(ct);
        var draft = await _db.LessonSuggestions.AsNoTracking().Include(s => s.Tags).Include(s => s.Pictures)
            .Where(s => s.RankId == rank.Id && s.Kind == LessonChangeKind.NewLesson && s.AuthorUserId == userId
                && (s.Status == LessonSuggestionStatus.Draft || s.Status == LessonSuggestionStatus.Submitted)
                && (suggestionId == null || s.Id == suggestionId))
            .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync(ct);
        if (draft is not null)
        {
            return new SuggestionEditor(null, null, "", rank.Id, rank.Name, null, null, ToDraftView(draft), canPublish, null);
        }

        var nextOrder = (await _db.Lessons.Where(l => l.RankId == rank.Id).Select(l => (int?)l.Order).MaxAsync(ct) ?? 0) + 1;
        var fresh = new SuggestionDraftView(null, null, LessonChangeKind.NewLesson, "", nextOrder, [], "", null,
            new Dictionary<string, string>(StringComparer.Ordinal), null);
        return new SuggestionEditor(null, null, "", rank.Id, rank.Name, null, null, fresh, canPublish, null);
    }

    /// <summary>
    /// Saves the caller's draft: creates it (taking the one open place for the lesson) or updates their own. Returns its id.
    /// A submitted suggestion cannot be changed.
    /// </summary>
    public async Task<int> SaveDraftAsync(SuggestionInput input, CancellationToken ct = default)
    {
        RequireSuggester();
        var suggestion = await SaveAsync(input, ct);
        await _db.SaveChangesAsync(ct);
        return suggestion.Id;
    }

    /// <summary>Saves and submits the caller's suggestion for review. The reason is required.</summary>
    public async Task<int> SubmitAsync(SuggestionInput input, CancellationToken ct = default)
    {
        RequireSuggester();
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            throw new InvalidOperationException("Give a reason for the change.");
        }

        var suggestion = await SaveAsync(input, ct);
        await ValidateForPublishingAsync(suggestion, ct);
        Set(suggestion, s => s.Status, LessonSuggestionStatus.Submitted);
        Set(suggestion, s => s.SubmittedAt, (DateTime?)DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        if (_notifications is not null)
        {
            await _notifications.NotifySubmittedAsync(suggestion.Id, ct);
        }
        return suggestion.Id;
    }

    /// <summary>
    /// "Publish now" (decision 10): an Approver publishes their own change at once, with no review, while the national
    /// setting allows direct publishing. The reason is optional. Returns the new version.
    /// </summary>
    public async Task<PublishedVersion> PublishNowAsync(SuggestionInput input, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireUserId();
        if (!await IsDirectPublishingAllowedAsync(ct))
        {
            throw new InvalidOperationException("Review is required for every change: submit it for review instead.");
        }

        var suggestion = await SaveAsync(input, ct);
        await ValidateForPublishingAsync(suggestion, ct);
        var version = await PublishAsync(suggestion, approverId, direct: true, ct);
        await _db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>Deletes the caller's own draft. A submitted suggestion cannot be discarded.</summary>
    public async Task DiscardAsync(int suggestionId, CancellationToken ct = default)
    {
        RequireSuggester();
        var userId = RequireUserId();
        var suggestion = await _db.LessonSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("The draft no longer exists.");
        if (suggestion.AuthorUserId != userId)
        {
            throw new UnauthorizedAccessException("Only its author may discard a draft.");
        }
        if (suggestion.Status != LessonSuggestionStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft can be discarded.");
        }

        _db.LessonSuggestions.Remove(suggestion);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A picture inserted in the editor into the caller's own draft: a JPEG or PNG, resized as lesson pictures are
    /// (<see cref="LessonPictureResizer"/>) and stored under a key never reused for other content. Returns the bare file
    /// name the lesson text refers to it by (made unique within the draft) and its address.
    /// </summary>
    public async Task<UploadedPicture> UploadPictureAsync(int suggestionId, string fileName, Stream content, CancellationToken ct = default)
    {
        RequireSuggester();
        var userId = RequireUserId();
        var suggestion = await _db.LessonSuggestions.Include(s => s.Pictures).SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("Save the draft before inserting a picture.");
        if (suggestion.AuthorUserId != userId)
        {
            throw new UnauthorizedAccessException("Only its author may add pictures to a draft.");
        }
        if (suggestion.Status != LessonSuggestionStatus.Draft)
        {
            throw new InvalidOperationException("A submitted suggestion cannot be changed.");
        }

        var name = SafePictureName(fileName);
        var bytes = await ReadBoundedAsync(content, ct);
        var resized = bytes.Length == 0 ? null : LessonPictureResizer.Resize(bytes);
        if (resized is null)
        {
            throw new InvalidOperationException("The picture must be a JPEG or PNG file.");
        }

        var taken = suggestion.Pictures.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; taken.Contains(name); n++)
        {
            name = $"{stem}-{n}{extension}";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(resized))[..16];
        var key = $"lesson-pictures/suggestions/{suggestion.Id}/{hash}/{name}";
        await _blobs.UploadAsync(key, resized, ct);
        _db.LessonSuggestionPictures.Add(new LessonSuggestionPicture
        {
            Id = await _db.NextIdAsync<LessonSuggestionPicture>(ct),
            LessonSuggestionId = suggestion.Id,
            FileName = name,
            BlobKey = key
        });
        Set(suggestion, s => s.UpdatedAt, DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return new UploadedPicture(name, SuggestionPictureUrl(suggestion.Id, name));
    }

    /// <summary>A suggestion's picture, for its author or an Approver; null if unknown or missing.</summary>
    public async Task<LessonFileContent?> OpenSuggestionPictureAsync(int suggestionId, string fileName, CancellationToken ct = default)
    {
        var isApprover = _currentUser.HasRole(Role.Approver);
        if (!isApprover && !CanSuggest)
        {
            throw new UnauthorizedAccessException("Caller may not open suggestion pictures.");
        }
        var userId = RequireUserId();

        var picture = await _db.LessonSuggestionPictures.AsNoTracking()
            .Where(p => p.LessonSuggestionId == suggestionId && p.FileName == fileName)
            .Select(p => new { p.FileName, p.BlobKey, p.LessonSuggestion!.AuthorUserId })
            .SingleOrDefaultAsync(ct);
        if (picture is null)
        {
            return null;
        }
        if (!isApprover && picture.AuthorUserId != userId)
        {
            throw new UnauthorizedAccessException("Only its author or an Approver may open this picture.");
        }

        var bytes = await _blobs.ReadAsync(picture.BlobKey, ct);
        return bytes is null ? null : new LessonFileContent(picture.FileName, bytes);
    }

    /// <summary>A version's picture, for Approvers and Admins (the history); null if unknown or missing.</summary>
    public async Task<LessonFileContent?> OpenVersionPictureAsync(int pictureId, CancellationToken ct = default)
    {
        RequireHistoryRole();
        var picture = await _db.LessonVersionPictures.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pictureId, ct);
        if (picture is null)
        {
            return null;
        }

        var bytes = await _blobs.ReadAsync(picture.BlobKey, ct);
        return bytes is null ? null : new LessonFileContent(picture.FileName, bytes);
    }

    // --- Review ----------------------------------------------------------------------------------------------------

    /// <summary>Approvers: every submitted suggestion, oldest first ("Changes to review"), referred ones included.</summary>
    public async Task<IReadOnlyList<SuggestionQueueItem>> GetQueueAsync(CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var userId = RequireUserId();
        var rows = await _db.LessonSuggestions.AsNoTracking()
            .Where(s => s.Status == LessonSuggestionStatus.Submitted)
            .Select(s => new
            {
                s.Id, s.Title, s.Kind, s.Reason, s.SubmittedAt, Author = s.AuthorUser != null ? s.AuthorUser.DisplayName : FormerStaff.Name,
                LessonTitle = s.Lesson != null ? s.Lesson.Title : null, s.RankId,
                Referred = s.ReferredAt != null,
                Approvals = s.CommitteeApprovals.Count(),
                ApprovedByMe = s.CommitteeApprovals.Any(a => a.ApproverUserId == userId)
            })
            .ToListAsync(ct);
        var ranks = await _db.Ranks.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return rows.OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id)
            .Select(r => new SuggestionQueueItem(r.Id, r.LessonTitle ?? r.Title, ranks.GetValueOrDefault(r.RankId, ""), r.Author,
                r.SubmittedAt ?? default, r.Reason, r.Kind, r.Referred, r.Approvals, r.ApprovedByMe))
            .ToList();
    }

    /// <summary>
    /// The number beside "Changes to review" in the menu: submitted suggestions awaiting the caller's decision, that is
    /// every one not referred to committee, and every referred one the caller has not yet approved. Zero for anyone who
    /// is not an Approver (the menu asks on every page, so this does not throw).
    /// </summary>
    public async Task<int> CountAwaitingMyReviewAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Approver) || _currentUser.UserId is not int userId)
        {
            return 0;
        }

        return await _db.LessonSuggestions.AsNoTracking()
            .CountAsync(s => s.Status == LessonSuggestionStatus.Submitted
                && (s.ReferredAt == null || !s.CommitteeApprovals.Any(a => a.ApproverUserId == userId)), ct);
    }

    /// <summary>Approvers: a suggestion laid out as tracked changes against the lesson's current version.</summary>
    public async Task<SuggestionReview?> GetReviewAsync(int suggestionId, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var userId = RequireUserId();
        var suggestion = await _db.LessonSuggestions.AsNoTracking()
            .Include(s => s.Tags).Include(s => s.Pictures).Include(s => s.AuthorUser).Include(s => s.DecidedByUser)
            .SingleOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion is null || suggestion.Status == LessonSuggestionStatus.Draft)
        {
            return null;
        }
        var committee = await CommitteeAsync(suggestion.Id, ct);

        var rankName = await _db.Ranks.Where(r => r.Id == suggestion.RankId).Select(r => r.Name).SingleAsync(ct);
        var current = suggestion.LessonId is int lessonId ? await LatestVersionAsync(lessonId, ct) : null;
        var lesson = suggestion.LessonId is int id ? await _db.Lessons.AsNoTracking().SingleAsync(l => l.Id == id, ct) : null;

        var before = current is null ? LessonDiff.Side.Empty : VersionSide(current);
        var after = SuggestionSide(suggestion);
        var isRetire = suggestion.Kind == LessonChangeKind.Retire;
        var currentNumber = current?.Number;

        return new SuggestionReview(
            suggestion.Id, suggestion.Kind, suggestion.Status, lesson?.Code, lesson?.Title ?? suggestion.Title, rankName,
            suggestion.AuthorUser?.DisplayName ?? FormerStaff.Name, suggestion.SubmittedAt ?? suggestion.UpdatedAt, suggestion.Reason,
            suggestion.BaseVersionNumber, currentNumber,
            IsStale: suggestion.LessonId is not null && suggestion.BaseVersionNumber != currentNumber,
            TrackedHtml: isRetire ? "" : LessonDiff.Tracked(before, after),
            FinalHtml: isRetire ? "" : LessonDiff.Render(after),
            BeforeHtml: LessonDiff.Render(before),
            SettingChanges: SettingChanges(current, suggestion.Title, suggestion.Order, suggestion.Tags.Select(t => t.Tag), rankName),
            FormerStaff.Or(suggestion.DecidedByUser?.DisplayName, suggestion.DecidedAt is not null), suggestion.DecidedAt, suggestion.SentBackComment,
            committee.Referral, committee.Approvals, committee.ApproverIds.Contains(userId));
    }

    /// <summary>
    /// Approvers: approves a submitted suggestion (their own included, decision 3). Adds the lesson's next version and
    /// makes it live. Refused when the lesson has changed since the suggestion was based on it.
    /// </summary>
    public async Task<PublishedVersion> ApproveAsync(int suggestionId, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireUserId();
        var suggestion = await _db.LessonSuggestions.Include(s => s.Tags).Include(s => s.Pictures)
            .SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("The suggestion no longer exists.");
        if (suggestion.Status != LessonSuggestionStatus.Submitted)
        {
            throw new InvalidOperationException("This suggestion is not awaiting review.");
        }
        if (suggestion.ReferredAt is not null)
        {
            throw new InvalidOperationException("This suggestion has been referred to committee, so it needs the committee's approval.");
        }

        await ValidateForPublishingAsync(suggestion, ct);
        var version = await PublishAsync(suggestion, approverId, direct: false, ct);
        await _db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>
    /// Approvers: refers a submitted suggestion to committee (decision 8), with an optional reason. It then needs
    /// <see cref="LessonSuggestionApproval.Required"/> different Approvers' approval (<see cref="ApproveReferredAsync"/>).
    /// Refused for a suggestion not awaiting review, one already referred, and one based on an older version of the
    /// lesson (it could never be approved).
    /// </summary>
    public async Task ReferAsync(int suggestionId, string? reason, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireUserId();
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
        {
            throw new InvalidOperationException($"The reason must be at most {MaxReasonLength} characters.");
        }

        var suggestion = await _db.LessonSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("The suggestion no longer exists.");
        if (suggestion.Status != LessonSuggestionStatus.Submitted)
        {
            throw new InvalidOperationException("This suggestion is not awaiting review.");
        }
        if (suggestion.ReferredAt is not null)
        {
            throw new InvalidOperationException("This suggestion has already been referred to committee.");
        }
        if (suggestion.LessonId is int lessonId && suggestion.BaseVersionNumber != await CurrentVersionNumberAsync(lessonId, ct))
        {
            throw new InvalidOperationException(
                "The lesson has changed since this suggestion was started, so it cannot be approved. Send it back instead.");
        }

        Set(suggestion, s => s.ReferredAt, (DateTime?)DateTime.UtcNow);
        Set(suggestion, s => s.ReferredByUserId, (int?)approverId);
        Set(suggestion, s => s.ReferralReason, trimmed);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Approvers: approves a suggestion referred to committee (decision 8). Each Approver may approve it once, the author
    /// included; on the <see cref="LessonSuggestionApproval.Required"/>th different approval it is published as an
    /// approved suggestion. Two Approvers approving at the same moment cannot both take the same place (a unique index):
    /// the second is refused and asked to try again.
    /// </summary>
    public async Task<CommitteeApprovalResult> ApproveReferredAsync(int suggestionId, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireUserId();
        var suggestion = await _db.LessonSuggestions.Include(s => s.Tags).Include(s => s.Pictures)
            .SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("The suggestion no longer exists.");
        if (suggestion.Status != LessonSuggestionStatus.Submitted)
        {
            throw new InvalidOperationException("This suggestion is not awaiting review.");
        }
        if (suggestion.ReferredAt is null)
        {
            throw new InvalidOperationException("This suggestion has not been referred to committee.");
        }

        var approvers = await _db.LessonSuggestionApprovals.AsNoTracking().Where(a => a.LessonSuggestionId == suggestion.Id)
            .Select(a => a.ApproverUserId).ToListAsync(ct);
        if (approvers.Contains(approverId))
        {
            throw new InvalidOperationException("You have already approved this suggestion.");
        }

        await ValidateForPublishingAsync(suggestion, ct);
        var position = approvers.Count + 1;
        var now = DateTime.UtcNow;
        _db.LessonSuggestionApprovals.Add(new LessonSuggestionApproval
        {
            Id = await _db.NextIdAsync<LessonSuggestionApproval>(ct),
            LessonSuggestionId = suggestion.Id,
            ApproverUserId = approverId,
            Position = position,
            ApprovedAt = now
        });
        var published = position >= LessonSuggestionApproval.Required
            ? await PublishAsync(suggestion, approverId, direct: false, ct)
            : null;
        Set(suggestion, s => s.UpdatedAt, now);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another Approver approved at the same moment. Nothing was saved; forget the failed changes so this
            // (circuit-scoped) context stays usable.
            _db.ChangeTracker.Clear();
            throw new InvalidOperationException("Another Approver acted on this suggestion at the same moment. Reload the page and try again.");
        }
        return new CommitteeApprovalResult(position, published);
    }

    /// <summary>Approvers: sends a submitted suggestion back to its author with a required comment.</summary>
    public async Task SendBackAsync(int suggestionId, string comment, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireUserId();
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException("Write a comment to send the suggestion back.");
        }

        var suggestion = await _db.LessonSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new InvalidOperationException("The suggestion no longer exists.");
        if (suggestion.Status != LessonSuggestionStatus.Submitted)
        {
            throw new InvalidOperationException("This suggestion is not awaiting review.");
        }

        Set(suggestion, s => s.Status, LessonSuggestionStatus.SentBack);
        Set(suggestion, s => s.SentBackComment, comment.Trim());
        Set(suggestion, s => s.DecidedByUserId, (int?)approverId);
        Set(suggestion, s => s.DecidedAt, (DateTime?)DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    // --- History ---------------------------------------------------------------------------------------------------

    /// <summary>Approvers and Admins: every version of a lesson, newest first, and its suggestions that were sent back.</summary>
    public async Task<LessonHistory?> GetHistoryAsync(string lessonCode, CancellationToken ct = default)
    {
        RequireHistoryRole();
        var lesson = await _db.Lessons.AsNoTracking().Include(l => l.Rank).SingleOrDefaultAsync(l => l.Code == lessonCode, ct);
        if (lesson is null)
        {
            return null;
        }

        var versions = await _db.LessonVersions.AsNoTracking().Include(v => v.Tags).Include(v => v.Pictures)
            .Where(v => v.LessonId == lesson.Id).OrderBy(v => v.Number).ToListAsync(ct);
        var names = await _db.AppUsers.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(int? id) => id is int value ? names.GetValueOrDefault(value) : null;

        var entries = new List<HistoryVersion>();
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var previous = i > 0 ? versions[i - 1] : null;
            string? changes = null, beforeHtml = null, afterHtml = null;
            IReadOnlyList<SettingChange> settings = [];
            if (previous is not null)
            {
                settings = SettingChanges(previous, version.Title, version.Order, version.Tags.Select(t => t.Tag), lesson.Rank!.Name,
                    version.IsActive);
                if (version.Kind == LessonVersionKind.ReplacedInFull)
                {
                    beforeHtml = LessonDiff.Render(VersionSide(previous));
                    afterHtml = LessonDiff.Render(VersionSide(version));
                }
                else
                {
                    changes = LessonDiff.Tracked(VersionSide(previous), VersionSide(version));
                }
            }

            var committee = version.SuggestionId is int sid ? await CommitteeAsync(sid, ct) : Committee.None;
            // Every version but a converted one came from a suggestion, so it names who suggested it and who approved or
            // published it; a missing name there is an account deleted under the retention rules (docs/adr/0014).
            var suggested = version.Kind != LessonVersionKind.Converted;
            entries.Add(new HistoryVersion(version.Number, i == versions.Count - 1, version.Kind, version.IsActive,
                FormerStaff.Or(Name(version.SuggestedByUserId), suggested), version.SuggestedAt,
                FormerStaff.Or(Name(version.ApprovedByUserId), suggested && !version.PublishedDirectly),
                FormerStaff.Or(Name(version.PublishedByUserId), suggested && version.PublishedDirectly),
                version.Reason, version.PublishedAt, changes, beforeHtml, afterHtml, settings, committee.Referral, committee.Approvals));
        }
        entries.Reverse();

        var sentBackRows = await _db.LessonSuggestions.AsNoTracking()
            .Where(s => s.LessonId == lesson.Id && s.Status == LessonSuggestionStatus.SentBack)
            .OrderByDescending(s => s.DecidedAt)
            .Select(s => new
            {
                s.Id, Author = s.AuthorUser != null ? s.AuthorUser.DisplayName : FormerStaff.Name, At = s.SubmittedAt ?? s.UpdatedAt, s.Reason,
                DecidedBy = s.DecidedByUser != null ? s.DecidedByUser.DisplayName : FormerStaff.Name, DecidedAt = s.DecidedAt ?? s.UpdatedAt, Comment = s.SentBackComment ?? ""
            })
            .ToListAsync(ct);
        var sentBack = new List<SentBackSuggestion>();
        foreach (var row in sentBackRows)
        {
            var committee = await CommitteeAsync(row.Id, ct);
            sentBack.Add(new SentBackSuggestion(row.Author, row.At, row.Reason, row.DecidedBy, row.DecidedAt, row.Comment,
                committee.Referral, committee.Approvals));
        }

        return new LessonHistory(lesson.Id, lesson.Code, lesson.Title, entries, sentBack);
    }

    // --- The national setting --------------------------------------------------------------------------------------

    /// <summary>Whether "Publish now" is allowed (decision 10). With no change recorded, it is: the setting for the first pass.</summary>
    public async Task<bool> IsDirectPublishingAllowedAsync(CancellationToken ct = default) =>
        await _db.LessonPublishingSettingChanges.AsNoTracking().OrderByDescending(c => c.Id)
            .Select(c => (bool?)c.DirectPublishingAllowed).FirstOrDefaultAsync(ct) ?? true;

    /// <summary>National Administrators: the setting and every change of it, newest first.</summary>
    public async Task<PublishingSetting> GetPublishingSettingAsync(CancellationToken ct = default)
    {
        RequireRole(Role.NationalAdministrator);
        var changes = await _db.LessonPublishingSettingChanges.AsNoTracking().OrderByDescending(c => c.Id)
            .Select(c => new PublishingSettingChange(c.DirectPublishingAllowed, c.ChangedByUser != null ? c.ChangedByUser.DisplayName : FormerStaff.Name, c.ChangedAt))
            .ToListAsync(ct);
        return new PublishingSetting(changes.Count == 0 || changes[0].DirectPublishingAllowed, changes);
    }

    /// <summary>National Administrators: sets "Direct publishing allowed" or "Review required", recording who and when.</summary>
    public async Task SetDirectPublishingAllowedAsync(bool allowed, CancellationToken ct = default)
    {
        RequireRole(Role.NationalAdministrator);
        var userId = RequireUserId();
        if (await IsDirectPublishingAllowedAsync(ct) == allowed
            && await _db.LessonPublishingSettingChanges.AnyAsync(ct))
        {
            return;
        }

        _db.LessonPublishingSettingChanges.Add(new LessonPublishingSettingChange
        {
            Id = await _db.NextIdAsync<LessonPublishingSettingChange>(ct),
            DirectPublishingAllowed = allowed,
            ChangedByUserId = userId,
            ChangedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    // --- Internals -------------------------------------------------------------------------------------------------

    private sealed record Committee(Referral? Referral, IReadOnlyList<CommitteeApproval> Approvals, IReadOnlyCollection<int> ApproverIds)
    {
        public static readonly Committee None = new(null, [], []);
    }

    // A suggestion's referral and its committee approvals, in the order given.
    private async Task<Committee> CommitteeAsync(int suggestionId, CancellationToken ct)
    {
        var referral = await _db.LessonSuggestions.AsNoTracking()
            .Where(s => s.Id == suggestionId && s.ReferredAt != null)
            .Select(s => new { s.ReferredAt, s.ReferredByUserId, s.ReferralReason })
            .SingleOrDefaultAsync(ct);
        if (referral is null)
        {
            return Committee.None;
        }

        var referredBy = await _db.AppUsers.AsNoTracking().Where(u => u.Id == referral.ReferredByUserId)
            .Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? FormerStaff.Name;
        var approvals = await _db.LessonSuggestionApprovals.AsNoTracking()
            .Where(a => a.LessonSuggestionId == suggestionId)
            .OrderBy(a => a.Position)
            .Select(a => new { a.ApproverUserId, Name = a.ApproverUser != null ? a.ApproverUser.DisplayName : FormerStaff.Name, a.ApprovedAt })
            .ToListAsync(ct);
        return new Committee(
            new Referral(referredBy, referral.ReferredAt!.Value, referral.ReferralReason),
            approvals.Select(a => new CommitteeApproval(a.Name, a.ApprovedAt)).ToList(),
            approvals.Select(a => a.ApproverUserId).OfType<int>().ToHashSet());
    }

    private async Task<bool> CanPublishNowAsync(CancellationToken ct) =>
        _currentUser.HasRole(Role.Approver) && await IsDirectPublishingAllowedAsync(ct);

    private async Task<int?> CurrentVersionNumberAsync(int lessonId, CancellationToken ct) =>
        await _db.LessonVersions.Where(v => v.LessonId == lessonId).Select(v => (int?)v.Number).MaxAsync(ct);

    private async Task<LessonVersion?> LatestVersionAsync(int lessonId, CancellationToken ct) =>
        await _db.LessonVersions.AsNoTracking().Include(v => v.Tags).Include(v => v.Pictures)
            .Where(v => v.LessonId == lessonId).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);

    private async Task<OpenSuggestion?> OpenSuggestionAsync(int lessonId, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var open = await _db.LessonSuggestions.AsNoTracking()
            .Where(s => s.LessonId == lessonId && (s.Status == LessonSuggestionStatus.Draft || s.Status == LessonSuggestionStatus.Submitted))
            .Select(s => new { s.Id, s.Status, s.AuthorUserId, Author = s.AuthorUser != null ? s.AuthorUser.DisplayName : FormerStaff.Name, s.CreatedAt, s.SubmittedAt })
            .FirstOrDefaultAsync(ct);
        return open is null
            ? null
            : new OpenSuggestion(open.Id, open.Status, open.Author, open.SubmittedAt ?? open.CreatedAt, open.AuthorUserId == userId);
    }

    private async Task<LessonSuggestion> LoadSuggestionAsync(int id, CancellationToken ct) =>
        await _db.LessonSuggestions.AsNoTracking().Include(s => s.Tags).Include(s => s.Pictures).SingleAsync(s => s.Id == id, ct);

    private async Task<SentBackInfo?> SentBackInfoAsync(int? suggestionId, CancellationToken ct) =>
        suggestionId is null
            ? null
            : await _db.LessonSuggestions.AsNoTracking().Where(s => s.Id == suggestionId)
                .Select(s => new SentBackInfo(s.DecidedByUser != null ? s.DecidedByUser.DisplayName : FormerStaff.Name, s.DecidedAt ?? s.UpdatedAt, s.SentBackComment ?? ""))
                .SingleOrDefaultAsync(ct);

    private static SuggestionDraftView ToDraftView(LessonSuggestion s) =>
        new(s.Id, s.Status, s.Kind, s.Title, s.Order, s.Tags.Select(t => t.Tag).Order().ToList(), s.Body, s.Reason,
            s.Pictures.ToDictionary(p => p.FileName, p => SuggestionPictureUrl(s.Id, p.FileName), StringComparer.Ordinal),
            s.RevisesSuggestionId);

    // Creates the caller's draft or updates it; not yet saved.
    private async Task<LessonSuggestion> SaveAsync(SuggestionInput input, CancellationToken ct)
    {
        var userId = RequireUserId();
        var now = DateTime.UtcNow;
        var title = (input.Title ?? "").Trim();
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (title.Length > MaxTitleLength)
        {
            throw new InvalidOperationException($"The title must be at most {MaxTitleLength} characters.");
        }
        if (reason is { Length: > MaxReasonLength })
        {
            throw new InvalidOperationException($"The reason must be at most {MaxReasonLength} characters.");
        }

        LessonSuggestion suggestion;
        if (input.SuggestionId is int id)
        {
            suggestion = await _db.LessonSuggestions.Include(s => s.Tags).Include(s => s.Pictures).SingleOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new InvalidOperationException("The draft no longer exists.");
            if (suggestion.AuthorUserId != userId)
            {
                throw new UnauthorizedAccessException("Only its author may change a draft.");
            }
            if (suggestion.Status != LessonSuggestionStatus.Draft)
            {
                throw new InvalidOperationException("A submitted suggestion cannot be changed.");
            }
            if (input.Kind == LessonChangeKind.NewLesson != (suggestion.Kind == LessonChangeKind.NewLesson))
            {
                throw new InvalidOperationException("A suggestion cannot change between a new lesson and an existing one.");
            }

            Set(suggestion, s => s.Kind, input.Kind);
            Set(suggestion, s => s.Title, title);
            Set(suggestion, s => s.Order, input.Order);
            Set(suggestion, s => s.Body, input.Body ?? "");
            Set(suggestion, s => s.Reason, reason);
            Set(suggestion, s => s.UpdatedAt, now);
            var wanted = input.Tags.Distinct().ToList();
            _db.LessonSuggestionTags.RemoveRange(suggestion.Tags.Where(t => !wanted.Contains(t.Tag)).ToList());
            foreach (var tag in wanted.Where(t => suggestion.Tags.All(e => e.Tag != t)))
            {
                _db.LessonSuggestionTags.Add(new LessonSuggestionTag { LessonSuggestionId = suggestion.Id, Tag = tag });
            }
            return suggestion;
        }

        int rankId;
        int? baseVersion = null;
        var pictures = new List<(string FileName, string BlobKey)>();
        if (input.Kind == LessonChangeKind.NewLesson)
        {
            if (input.LessonId is not null)
            {
                throw new InvalidOperationException("A new lesson must not refer to an existing lesson.");
            }
            rankId = await _db.Ranks.Where(r => r.Id == input.RankId).Select(r => (int?)r.Id).SingleOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Unknown rank.");
        }
        else
        {
            var lessonId = input.LessonId ?? throw new InvalidOperationException("Choose the lesson to change.");
            var lesson = await _db.Lessons.AsNoTracking().Include(l => l.Tags).Include(l => l.Pictures)
                .SingleOrDefaultAsync(l => l.Id == lessonId && l.IsActive && l.Body != null, ct)
                ?? throw new InvalidOperationException("The lesson no longer exists.");
            if (await _db.LessonSuggestions.AnyAsync(s => s.LessonId == lessonId
                    && (s.Status == LessonSuggestionStatus.Draft || s.Status == LessonSuggestionStatus.Submitted), ct))
            {
                throw new InvalidOperationException("A change to this lesson is already in progress.");
            }

            rankId = lesson.RankId;
            baseVersion = await EnsureBaselineVersionAsync(lesson, ct);
            if (input.BaseVersionNumber is int opened && opened != baseVersion)
            {
                throw new InvalidOperationException("The lesson has changed since you opened it. Open Suggest changes again to start from the latest version.");
            }
            pictures.AddRange(lesson.Pictures.Select(p => (p.FileName, p.BlobKey)));

            if (input.RevisesSuggestionId is int revises)
            {
                var original = await _db.LessonSuggestions.AsNoTracking().Include(s => s.Pictures).SingleOrDefaultAsync(s => s.Id == revises, ct);
                if (original is null || original.AuthorUserId != userId || original.LessonId != lessonId
                    || original.Status != LessonSuggestionStatus.SentBack)
                {
                    throw new InvalidOperationException("Only your own suggestion that was sent back can be carried on.");
                }
                foreach (var picture in original.Pictures.Where(p => pictures.All(x => x.FileName != p.FileName)))
                {
                    pictures.Add((picture.FileName, picture.BlobKey));
                }
            }
        }

        suggestion = new LessonSuggestion
        {
            Id = await _db.NextIdAsync<LessonSuggestion>(ct),
            LessonId = input.Kind == LessonChangeKind.NewLesson ? null : input.LessonId,
            RankId = rankId,
            Kind = input.Kind,
            Status = LessonSuggestionStatus.Draft,
            BaseVersionNumber = baseVersion,
            AuthorUserId = userId,
            Title = title,
            Order = input.Order,
            Body = input.Body ?? "",
            Reason = reason,
            CreatedAt = now,
            UpdatedAt = now,
            RevisesSuggestionId = input.Kind == LessonChangeKind.NewLesson ? null : input.RevisesSuggestionId
        };
        _db.LessonSuggestions.Add(suggestion);
        foreach (var tag in input.Tags.Distinct())
        {
            _db.LessonSuggestionTags.Add(new LessonSuggestionTag { LessonSuggestionId = suggestion.Id, Tag = tag });
        }
        var nextPictureId = await _db.NextIdAsync<LessonSuggestionPicture>(ct);
        foreach (var (fileName, blobKey) in pictures)
        {
            var picture = new LessonSuggestionPicture { Id = nextPictureId++, LessonSuggestionId = suggestion.Id, FileName = fileName, BlobKey = blobKey };
            _db.LessonSuggestionPictures.Add(picture);
            if (!suggestion.Pictures.Contains(picture))
            {
                suggestion.Pictures.Add(picture);
            }
        }
        return suggestion;
    }

    // A lesson written before versions were kept (or seeded without the seeder) gets its version 1 now, as it is.
    private async Task<int> EnsureBaselineVersionAsync(Lesson lesson, CancellationToken ct)
    {
        var current = await CurrentVersionNumberAsync(lesson.Id, ct);
        if (current is int number)
        {
            return number;
        }

        var versionId = await _db.NextIdAsync<LessonVersion>(ct);
        _db.LessonVersions.Add(new LessonVersion
        {
            Id = versionId,
            LessonId = lesson.Id,
            Number = 1,
            Kind = LessonVersionKind.Converted,
            Title = lesson.Title,
            Order = lesson.Order,
            IsActive = lesson.IsActive,
            Body = lesson.Body,
            ContentVersion = lesson.ContentVersion,
            PublishedAt = DateTime.UtcNow
        });
        foreach (var tag in lesson.Tags.Select(t => t.Tag).Distinct())
        {
            _db.LessonVersionTags.Add(new LessonVersionTag { LessonVersionId = versionId, Tag = tag });
        }
        var pictureId = await _db.NextIdAsync<LessonVersionPicture>(ct);
        foreach (var picture in lesson.Pictures.OrderBy(p => p.Order))
        {
            _db.LessonVersionPictures.Add(new LessonVersionPicture
            {
                Id = pictureId++, LessonVersionId = versionId, Order = picture.Order, FileName = picture.FileName, BlobKey = picture.BlobKey
            });
        }
        // Saved at once, so the suggestion's base and the stale check read the same version.
        await _db.SaveChangesAsync(ct);
        return 1;
    }

    private async Task ValidateForPublishingAsync(LessonSuggestion suggestion, CancellationToken ct)
    {
        if (suggestion.Title.Trim().Length == 0)
        {
            throw new InvalidOperationException("Give the lesson a title.");
        }
        if (suggestion.Order < 1)
        {
            throw new InvalidOperationException("The lesson's order must be 1 or more.");
        }
        var tagCount = _db.ChangeTracker.Entries<LessonSuggestionTag>()
            .Count(e => e.Entity.LessonSuggestionId == suggestion.Id && e.State != EntityState.Deleted && e.State != EntityState.Detached);
        if (tagCount == 0)
        {
            throw new InvalidOperationException("Choose at least one tag.");
        }

        if (suggestion.Kind != LessonChangeKind.Retire)
        {
            if (string.IsNullOrWhiteSpace(suggestion.Body))
            {
                throw new InvalidOperationException("The lesson text is empty.");
            }

            var available = suggestion.Pictures.Select(p => p.FileName).ToHashSet(StringComparer.Ordinal);
            foreach (var (url, alt) in LessonMarkdown.Pictures(suggestion.Body))
            {
                if (!LessonMarkdown.IsBareFileName(url) || !available.Contains(url))
                {
                    throw new InvalidOperationException($"The picture '{url}' is not one of this lesson's pictures. Insert it with \"Insert picture\".");
                }
                if (alt.Length == 0)
                {
                    throw new InvalidOperationException($"The picture '{url}' needs a description.");
                }
            }
        }

        if (suggestion.LessonId is int lessonId)
        {
            var current = await CurrentVersionNumberAsync(lessonId, ct);
            if (suggestion.BaseVersionNumber != current)
            {
                throw new InvalidOperationException(
                    "The lesson has changed since this suggestion was started, so it cannot be approved. Discard it and suggest changes again.");
            }
            if (!await _db.Lessons.AnyAsync(l => l.Id == lessonId && l.IsActive, ct))
            {
                throw new InvalidOperationException("The lesson has been retired.");
            }
        }
    }

    // Adds the next version, makes it the live lesson and marks the suggestion approved. Not yet saved.
    private async Task<PublishedVersion> PublishAsync(LessonSuggestion suggestion, int deciderId, bool direct, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tags = _db.ChangeTracker.Entries<LessonSuggestionTag>()
            .Where(e => e.Entity.LessonSuggestionId == suggestion.Id && e.State != EntityState.Deleted && e.State != EntityState.Detached)
            .Select(e => e.Entity.Tag).Distinct().Order().ToList();

        Lesson lesson;
        int number;
        if (suggestion.Kind == LessonChangeKind.NewLesson)
        {
            var rank = await _db.Ranks.AsNoTracking().SingleAsync(r => r.Id == suggestion.RankId, ct);
            lesson = new Lesson
            {
                Id = await _db.NextIdAsync<Lesson>(ct),
                RankId = rank.Id,
                Code = await NewLessonCodeAsync(rank, ct),
                Title = suggestion.Title.Trim(),
                Order = suggestion.Order,
                IsActive = true
            };
            _db.Lessons.Add(lesson);
            number = 1;
            Set(suggestion, s => s.LessonId, (int?)lesson.Id);
        }
        else
        {
            lesson = await _db.Lessons.SingleAsync(l => l.Id == suggestion.LessonId, ct);
            number = (await CurrentVersionNumberAsync(lesson.Id, ct) ?? 0) + 1;
        }

        var retire = suggestion.Kind == LessonChangeKind.Retire;
        var body = retire ? lesson.Body : suggestion.Body;
        var title = retire ? lesson.Title : suggestion.Title.Trim();
        var order = retire ? lesson.Order : suggestion.Order;

        // The pictures the text uses, in order of first appearance.
        List<(string FileName, string BlobKey)> pictures;
        if (retire)
        {
            pictures = await _db.LessonPictures.AsNoTracking().Where(p => p.LessonId == lesson.Id).OrderBy(p => p.Order)
                .Select(p => new ValueTuple<string, string>(p.FileName, p.BlobKey)).ToListAsync(ct);
        }
        else
        {
            var byName = suggestion.Pictures.DistinctBy(p => p.FileName, StringComparer.Ordinal).ToDictionary(p => p.FileName, p => p.BlobKey, StringComparer.Ordinal);
            pictures = LessonMarkdown.PictureReferences(body!).Distinct(StringComparer.Ordinal)
                .Where(byName.ContainsKey).Select(name => (name, byName[name])).ToList();
        }

        var contentVersion = ContentVersionOf(lesson.Id, number, body, pictures);

        Set(lesson, l => l.Title, title);
        Set(lesson, l => l.Order, order);
        Set(lesson, l => l.Body, body);
        Set(lesson, l => l.ContentVersion, contentVersion);
        if (retire)
        {
            Set(lesson, l => l.IsActive, false);
        }

        if (!retire)
        {
            var existingTags = await _db.LessonTagAssignments.Where(t => t.LessonId == lesson.Id).ToListAsync(ct);
            _db.LessonTagAssignments.RemoveRange(existingTags.Where(t => !tags.Contains(t.Tag)));
            foreach (var tag in tags.Where(t => existingTags.All(e => e.Tag != t)))
            {
                _db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = lesson.Id, Tag = tag });
            }

            // Live pictures, matched on file name so ids stay stable where nothing changed.
            var live = await _db.LessonPictures.Where(p => p.LessonId == lesson.Id).ToListAsync(ct);
            _db.LessonPictures.RemoveRange(live.Where(p => pictures.All(x => x.FileName != p.FileName)));
            var nextLivePictureId = await _db.NextIdAsync<LessonPicture>(ct);
            for (var i = 0; i < pictures.Count; i++)
            {
                var (fileName, blobKey) = pictures[i];
                var match = live.FirstOrDefault(p => p.FileName == fileName);
                if (match is null)
                {
                    _db.LessonPictures.Add(new LessonPicture { Id = nextLivePictureId++, LessonId = lesson.Id, Order = i + 1, FileName = fileName, BlobKey = blobKey });
                }
                else
                {
                    Set(match, p => p.Order, i + 1);
                    Set(match, p => p.BlobKey, blobKey);
                }
            }
        }

        var kind = suggestion.Kind == LessonChangeKind.ReplaceInFull
            ? LessonVersionKind.ReplacedInFull
            : direct ? LessonVersionKind.PublishedDirectly : LessonVersionKind.ApprovedSuggestion;
        var versionId = await _db.NextIdAsync<LessonVersion>(ct);
        _db.LessonVersions.Add(new LessonVersion
        {
            Id = versionId,
            LessonId = lesson.Id,
            Number = number,
            Kind = kind,
            Title = title,
            Order = order,
            IsActive = !retire,
            Body = body,
            ContentVersion = contentVersion,
            SuggestedByUserId = suggestion.AuthorUserId,
            SuggestedAt = suggestion.SubmittedAt is DateTime submitted ? DateTime.SpecifyKind(submitted, DateTimeKind.Utc) : now,
            ApprovedByUserId = direct ? null : deciderId,
            PublishedByUserId = direct ? deciderId : null,
            PublishedDirectly = direct,
            Reason = suggestion.Reason,
            PublishedAt = now,
            SuggestionId = suggestion.Id
        });
        foreach (var tag in retire ? await _db.LessonTagAssignments.Where(t => t.LessonId == lesson.Id).Select(t => t.Tag).ToListAsync(ct) : tags)
        {
            _db.LessonVersionTags.Add(new LessonVersionTag { LessonVersionId = versionId, Tag = tag });
        }
        var nextVersionPictureId = await _db.NextIdAsync<LessonVersionPicture>(ct);
        for (var i = 0; i < pictures.Count; i++)
        {
            _db.LessonVersionPictures.Add(new LessonVersionPicture
            {
                Id = nextVersionPictureId++, LessonVersionId = versionId, Order = i + 1, FileName = pictures[i].FileName, BlobKey = pictures[i].BlobKey
            });
        }

        Set(suggestion, s => s.Status, LessonSuggestionStatus.Approved);
        Set(suggestion, s => s.DecidedByUserId, (int?)deciderId);
        Set(suggestion, s => s.DecidedAt, (DateTime?)now);
        if (suggestion.SubmittedAt is null)
        {
            Set(suggestion, s => s.SubmittedAt, (DateTime?)now);
        }

        return new PublishedVersion(lesson.Id, lesson.Code, number, kind);
    }

    /// <summary>
    /// A lesson's content version after publishing version <paramref name="number"/>: a hash of the lesson, the version
    /// number, its text and its pictures, so it always changes when a version is published (FR-023, adr/0012 decision 7).
    /// </summary>
    public static string ContentVersionOf(int lessonId, int number, string? body, IEnumerable<(string FileName, string BlobKey)> pictures)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"lesson {lessonId} version {number}\0{body}"));
        foreach (var (fileName, blobKey) in pictures)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\0{fileName}\0{blobKey}"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // R{rank order}-{next number}, the pattern of the curriculum's codes (R5-03), skipping any code already used.
    private async Task<string> NewLessonCodeAsync(Rank rank, CancellationToken ct)
    {
        var prefix = $"R{rank.Order}-";
        var codes = await _db.Lessons.Select(l => l.Code).ToListAsync(ct);
        var next = codes.Where(c => c.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        string code;
        while (codes.Contains(code = $"{prefix}{next:00}"))
        {
            next++;
        }
        return code;
    }

    private IReadOnlyList<SettingChange> SettingChanges(LessonVersion? before, string title, int order, IEnumerable<LessonTag> tags,
        string rankName, bool isActive = true)
    {
        if (before is null)
        {
            return [];
        }

        var changes = new List<SettingChange>();
        if (before.Title != title.Trim())
        {
            changes.Add(new SettingChange("Title", LessonDiff.TrackedText(before.Title, title.Trim())));
        }
        if (before.Order != order)
        {
            changes.Add(new SettingChange($"Order in {rankName}", LessonDiff.TrackedText(before.Order.ToString(), order.ToString())));
        }
        var beforeTags = string.Join(", ", before.Tags.Select(t => t.Tag).Order().Select(LessonTagNames.Display));
        var afterTags = string.Join(", ", tags.Distinct().Order().Select(LessonTagNames.Display));
        if (beforeTags != afterTags)
        {
            changes.Add(new SettingChange("Tags", LessonDiff.TrackedText(beforeTags, afterTags)));
        }
        if (before.IsActive && !isActive)
        {
            changes.Add(new SettingChange("Retired", "Yes"));
        }
        return changes;
    }

    private static LessonDiff.Side VersionSide(LessonVersion version) =>
        new(version.Body, version.Pictures.ToDictionary(p => p.FileName, p => (p.BlobKey, VersionPictureUrl(p.Id)), StringComparer.Ordinal));

    private static LessonDiff.Side SuggestionSide(LessonSuggestion suggestion) =>
        new(suggestion.Body, suggestion.Pictures.ToDictionary(p => p.FileName, p => (p.BlobKey, SuggestionPictureUrl(suggestion.Id, p.FileName)),
            StringComparer.Ordinal));

    private static string SafePictureName(string fileName)
    {
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!LessonPictureResizer.SupportedExtensions.Contains(extension))
        {
            throw new InvalidOperationException("The picture must be a JPEG or PNG file.");
        }

        var stem = new string(Path.GetFileNameWithoutExtension(name).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }
        if (stem.Length == 0)
        {
            stem = "picture";
        }
        return (stem.Length > 80 ? stem[..80] : stem) + extension;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxPictureBytes)
            {
                throw new InvalidOperationException($"The picture is larger than {MaxPictureBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Entities are init-only, so existing rows are changed through the change tracker (openwiki/curriculum.md).
    private void Set<TEntity, TValue>(TEntity entity, System.Linq.Expressions.Expression<Func<TEntity, TValue>> property, TValue value)
        where TEntity : class
        => _db.Entry(entity).Property(property).CurrentValue = value;

    private void RequireSuggester()
    {
        if (!CanSuggest)
        {
            throw new UnauthorizedAccessException("Caller does not hold a role that may suggest lesson changes.");
        }
    }

    private void RequireHistoryRole()
    {
        if (!CanReadHistory)
        {
            throw new UnauthorizedAccessException("Caller does not hold a role that may read lesson history.");
        }
    }

    private void RequireRole(Role role)
    {
        if (!_currentUser.HasRole(role))
        {
            throw new UnauthorizedAccessException($"Caller does not hold the {role} role.");
        }
    }

    private int RequireUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
}

/// <summary>
/// What a caller enters in the editor. <see cref="SuggestionId"/> is null until the draft is first saved;
/// <see cref="BaseVersionNumber"/> is the version the editor was opened on, so a draft is never based on a later one.
/// </summary>
public sealed record SuggestionInput(
    int? SuggestionId,
    int? LessonId,
    int RankId,
    LessonChangeKind Kind,
    string Title,
    int Order,
    IReadOnlyCollection<LessonTag> Tags,
    string Body,
    string? Reason,
    int? RevisesSuggestionId = null,
    int? BaseVersionNumber = null);

public sealed record OpenSuggestion(int Id, LessonSuggestionStatus Status, string AuthorName, DateTime Date, bool IsMine);

public sealed record LessonPageSuggestions(int? VersionNumber, bool CanSuggest, bool CanReadHistory, OpenSuggestion? Open);

public sealed record SentBackInfo(string SentBackBy, DateTime SentBackAt, string Comment);

/// <summary>The editor's content; <see cref="Id"/> and <see cref="Status"/> are null for a draft not yet saved.</summary>
public sealed record SuggestionDraftView(
    int? Id,
    LessonSuggestionStatus? Status,
    LessonChangeKind Kind,
    string Title,
    int Order,
    IReadOnlyList<LessonTag> Tags,
    string Body,
    string? Reason,
    IReadOnlyDictionary<string, string> PictureUrls,
    int? RevisesSuggestionId);

public sealed record SuggestionEditor(
    int? LessonId,
    string? LessonCode,
    string LessonTitle,
    int RankId,
    string RankName,
    int? CurrentVersionNumber,
    OpenSuggestion? BlockedBy,
    SuggestionDraftView? Draft,
    bool CanPublishNow,
    SentBackInfo? SentBack);

public sealed record UploadedPicture(string FileName, string Url);

public sealed record PublishedVersion(int LessonId, string LessonCode, int Number, LessonVersionKind Kind);

/// <summary>
/// A row of "Changes to review". <see cref="CommitteeApprovals"/> counts the approvals of a referred suggestion, and
/// <see cref="ApprovedByMe"/> says whether the caller has given one.
/// </summary>
public sealed record SuggestionQueueItem(int Id, string LessonTitle, string RankName, string SuggestedBy, DateTime SubmittedAt, string? Reason,
    LessonChangeKind Kind, bool IsReferred = false, int CommitteeApprovals = 0, bool ApprovedByMe = false);

/// <summary>A referral to committee: who referred the suggestion, when, and the optional reason (docs/adr/0013 decision 8).</summary>
public sealed record Referral(string ReferredBy, DateTime ReferredAt, string? Reason);

/// <summary>One committee approval of a referred suggestion.</summary>
public sealed record CommitteeApproval(string ApprovedBy, DateTime ApprovedAt);

/// <summary>
/// The outcome of a committee approval: how many Approvers have now approved, and the new version once the
/// <see cref="LessonSuggestionApproval.Required"/>th has (null before then).
/// </summary>
public sealed record CommitteeApprovalResult(int Approvals, PublishedVersion? Published);

/// <summary>A change to the lesson's settings, as tracked-changes HTML.</summary>
public sealed record SettingChange(string Label, string Html);

public sealed record SuggestionReview(
    int Id,
    LessonChangeKind Kind,
    LessonSuggestionStatus Status,
    string? LessonCode,
    string LessonTitle,
    string RankName,
    string SuggestedBy,
    DateTime SubmittedAt,
    string? Reason,
    int? BaseVersionNumber,
    int? CurrentVersionNumber,
    bool IsStale,
    string TrackedHtml,
    string FinalHtml,
    string BeforeHtml,
    IReadOnlyList<SettingChange> SettingChanges,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? SentBackComment,
    Referral? Referral = null,
    IReadOnlyList<CommitteeApproval>? CommitteeApprovals = null,
    bool ApprovedByMe = false);

public sealed record HistoryVersion(
    int Number,
    bool IsCurrent,
    LessonVersionKind Kind,
    bool IsActive,
    string? SuggestedBy,
    DateTime? SuggestedAt,
    string? ApprovedBy,
    string? PublishedBy,
    string? Reason,
    DateTime PublishedAt,
    string? ChangesHtml,
    string? BeforeHtml,
    string? AfterHtml,
    IReadOnlyList<SettingChange> SettingChanges,
    Referral? Referral = null,
    IReadOnlyList<CommitteeApproval>? CommitteeApprovals = null);

public sealed record SentBackSuggestion(string SuggestedBy, DateTime SuggestedAt, string? Reason, string SentBackBy, DateTime SentBackAt, string Comment,
    Referral? Referral = null, IReadOnlyList<CommitteeApproval>? CommitteeApprovals = null);

public sealed record LessonHistory(int LessonId, string Code, string Title, IReadOnlyList<HistoryVersion> Versions, IReadOnlyList<SentBackSuggestion> SentBack);

public sealed record PublishingSettingChange(bool DirectPublishingAllowed, string ChangedBy, DateTime ChangedAt);

public sealed record PublishingSetting(bool DirectPublishingAllowed, IReadOnlyList<PublishingSettingChange> Changes);
