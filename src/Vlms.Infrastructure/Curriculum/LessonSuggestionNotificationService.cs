using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// The email to Approvers when a lesson suggestion is submitted (FR-007, docs/adr/0013 decision 8), following
/// <see cref="Registration.RegistrationNotificationService"/>: every Approver who has not left hears of it (the Approver
/// role is national, so there is no chapter to narrow it to), the author included when they are an Approver, since they
/// may approve their own. The email carries the lesson, its rank, the author and a link built from <c>App:PublicBaseUrl</c>;
/// not the change itself, which is read after signing in.
///
/// <b>Failure isolation.</b> Called after the submission is saved. Never throws (cancellation aside): a failure is logged
/// and the submission stands.
/// </summary>
public sealed class LessonSuggestionNotificationService
{
    private readonly VlmsDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly ILogger<LessonSuggestionNotificationService> _logger;
    private readonly string? _publicBaseUrl;

    public LessonSuggestionNotificationService(
        VlmsDbContext db,
        INotificationService notificationService,
        ILogger<LessonSuggestionNotificationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _notificationService = notificationService;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>Tells every Approver who has not left that a suggestion is waiting for review.</summary>
    public async Task NotifySubmittedAsync(int suggestionId, CancellationToken ct = default)
    {
        try
        {
            var suggestion = await _db.LessonSuggestions.AsNoTracking()
                .Where(s => s.Id == suggestionId)
                .Select(s => new
                {
                    s.Kind,
                    Title = s.Lesson != null ? s.Lesson.Title : s.Title,
                    Author = s.AuthorUser != null ? s.AuthorUser.DisplayName : FormerStaff.Name,
                    s.RankId
                })
                .SingleAsync(ct);
            var rank = await _db.Ranks.AsNoTracking().Where(r => r.Id == suggestion.RankId).Select(r => r.Name).SingleAsync(ct);

            var what = suggestion.Kind switch
            {
                LessonChangeKind.NewLesson => $"a new lesson, \"{suggestion.Title}\", in {rank}",
                LessonChangeKind.Retire => $"retiring \"{suggestion.Title}\" ({rank})",
                _ => $"a change to \"{suggestion.Title}\" ({rank})"
            };
            var subject = $"VLMS: change to review ({suggestion.Title})";
            var body = $"{suggestion.Author} has suggested {what}. It is waiting for review."
                + (_publicBaseUrl is null ? "" : $"\n\nReview it here: {_publicBaseUrl}{LessonSuggestionService.ReviewUrl(suggestionId)}")
                + "\n\nVarangians";

            var approvers = await _db.AppUsers.AsNoTracking()
                .Where(u => u.Roles.Any(r => r.Role == Role.Approver))
                .OrderBy(u => u.Id)
                .Select(u => new { u.Email, u.DisplayName })
                .ToListAsync(ct);
            foreach (var approver in approvers.Where(a => !string.IsNullOrWhiteSpace(a.Email)))
            {
                await _notificationService.SendAsync(
                    new NotificationRequest(approver.Email, approver.DisplayName, subject, body, NotificationPriority.Standard), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Emails to Approvers about suggestion {SuggestionId} failed; the submission stands.", suggestionId);
        }
    }
}
