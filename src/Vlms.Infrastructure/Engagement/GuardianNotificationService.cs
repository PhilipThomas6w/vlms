using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure.Safeguarding;

namespace Vlms.Infrastructure.Engagement;

/// <summary>
/// Proactive parent notifications (functional.md "Parent engagement (MVP)", BR-005): email to a
/// student's guardians on lesson completion and rank promotion, and consent-expiry reminders.
///
/// <b>Recipients.</b> Only guardians linked to the student by <see cref="StudentGuardianLink"/>
/// whose <see cref="ParentGuardian.AppUserId"/> account holds <see cref="Role.Parent"/> (i.e. an
/// Admin has approved them, FR-004) and has not left. A guardian with no account, or one still
/// waiting for approval, gets nothing; the free-text <see cref="ParentGuardian.ContactInfo"/> is
/// never used as an address.
///
/// <b>Content.</b> Student name, lesson title, rank name and consent expiry date only — all
/// non-restricted data. Nothing from <see cref="DbsCheck"/> or <see cref="ConsentSensitiveDetails"/>
/// is read here (adr/0004). A dashboard link is added when <c>App:PublicBaseUrl</c> is configured.
///
/// <b>Failure isolation.</b> <see cref="NotifyCompletionAsync"/> runs after the completion,
/// promotion and certificate are saved, and never throws (cancellation aside): a lookup or send
/// failure is logged and the Teacher's action stands. <see cref="INotificationService"/> itself
/// never throws.
///
/// <b>Reminder schedule</b> (adr/0008 answer 3). For each Active student who has not left, to
/// their linked parents (parents who have left get nothing, FR-017): (1) a renewal email when the
/// consent in force expires in exactly 30, 14, 7 or 1 days (31 August for consent issued under the
/// church-year rule, see <see cref="ConsentYear"/>) and no later-expiring approved consent exists;
/// (2) a weekly email, every <see cref="WeeklyReminderDay"/>, while the child has no consent in
/// force or the latest form was rejected and awaits resubmission; plus one
/// <see cref="NotificationPriority.SafeguardingCritical"/> notice the day after the last approved
/// consent ends, which replaces (never doubles) the weekly email if that day is a Monday. Nothing is sent while a submitted
/// form is Pending review. The weekday is fixed so no sent-reminder state is stored (no migration):
/// a missed daily run skips that day's reminder. If the latest form was rejected, the weekly email
/// replaces any renewal email for that day. Links go to <c>/parent/consent</c>. Each student is
/// processed in its own try/catch so one failure never stops the run.
/// </summary>
public sealed class GuardianNotificationService
{
    public static readonly IReadOnlyList<int> ReminderDaysBeforeExpiry = [30, 14, 7, 1];

    /// <summary>The weekday of the weekly reminder: fixed, so the daily job needs no stored state.</summary>
    public const DayOfWeek WeeklyReminderDay = DayOfWeek.Monday;

    private const string ConsentPath = "/parent/consent";

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly INotificationService _notificationService;
    private readonly ILogger<GuardianNotificationService> _logger;
    private readonly string? _publicBaseUrl;

    public GuardianNotificationService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        INotificationService notificationService,
        ILogger<GuardianNotificationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Emails the student's guardians that a lesson was completed and, when
    /// <paramref name="promoted"/>, a second email naming the new rank (or programme completion).
    /// Called by <c>CompletionService.MarkCompleteAsync</c> after everything is saved.
    /// </summary>
    public async Task<IReadOnlyList<NotificationOutcome>> NotifyCompletionAsync(
        int completionId, bool promoted, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);

        try
        {
            var completion = await _db.StudentLessonCompletions
                .Where(c => c.Id == completionId)
                .Select(c => new { c.StudentId, LessonTitle = c.Lesson!.Title })
                .SingleAsync(ct);
            var student = await _db.Students
                .Where(s => s.Id == completion.StudentId)
                .Select(s => new { s.Name, s.Status, RankName = s.CurrentRank!.Name })
                .SingleAsync(ct);

            var messages = new List<(string Subject, string Body)>
            {
                ($"VLMS: {student.Name} completed \"{completion.LessonTitle}\"",
                    $"{student.Name} has completed the lesson \"{completion.LessonTitle}\".")
            };

            if (promoted)
            {
                messages.Add(student.Status == StudentStatus.Graduated
                    ? ($"VLMS: {student.Name} has completed the programme",
                        $"{student.Name} has completed every rank in the programme. Congratulations!")
                    : ($"VLMS: {student.Name} has been promoted to {student.RankName}",
                        $"{student.Name} has completed their rank and been promoted to {student.RankName}."));
            }

            return await SendToGuardiansAsync(completion.StudentId, messages, NotificationPriority.Standard, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Guardian notification for completion {CompletionId} failed; the completion stands.", completionId);
            return [];
        }
    }

    /// <summary>
    /// Sends the parent consent reminders for <paramref name="today"/>, on the schedule in the class
    /// comment. Admin/SafeguardingOfficer only, the same gate as <see cref="ConsentExpiryNotifier"/>.
    /// Works from the consent records directly, not the daily sweep's flags: the sweep's warning
    /// window (28 days by default) is shorter than the 30-day reminder.
    /// </summary>
    public async Task<IReadOnlyList<NotificationOutcome>> SendConsentRemindersAsync(
        DateOnly today, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to send consent-expiry reminders.");
        }

        var isWeeklyDay = today.DayOfWeek == WeeklyReminderDay;
        var students = await _db.Students
            .Where(s => s.Status == StudentStatus.Active && s.LeftAt == null)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);
        var records = (await _db.ConsentRecords
                .Select(c => new { c.Id, c.StudentId, c.Status, c.ExpiryDate })
                .ToListAsync(ct))
            .ToLookup(c => c.StudentId);

        var outcomes = new List<NotificationOutcome>();
        foreach (var student in students)
        {
            try
            {
                var mine = records[student.Id].ToList();
                var inForce = mine
                    .Where(c => c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
                    .OrderByDescending(c => c.ExpiryDate)
                    .FirstOrDefault();
                var awaitingReview = mine.Any(c => c.Status == ConsentStatus.Pending);
                var latestRejected = mine.OrderByDescending(c => c.Id).FirstOrDefault()?.Status == ConsentStatus.Rejected;

                // Nothing for the parent to do while staff are reviewing a submitted form.
                if (awaitingReview)
                {
                    continue;
                }

                (string Subject, string Body)? message = null;
                var priority = NotificationPriority.Standard;
                var lapsedYesterday = inForce is null
                    && mine.Any(c => c.Status == ConsentStatus.Approved && c.ExpiryDate == today.AddDays(-1));
                if (lapsedYesterday)
                {
                    // Sent the day after the last approved consent ends (consent is valid through its
                    // ExpiryDate), so a lapse never waits for the weekly email. On a Monday this is the
                    // only email: the weekly one is not sent as well.
                    priority = NotificationPriority.SafeguardingCritical;
                    var lapsed = today.AddDays(-1);
                    message = ($"VLMS: {student.Name}'s consent has expired",
                        $"The annual consent for {student.Name} expired on {lapsed:dd/MM/yyyy}. " +
                        "Lessons cannot be recorded until it is renewed. Please renew it as soon as possible.");
                }
                else if (inForce is null || latestRejected)
                {
                    if (isWeeklyDay)
                    {
                        message = latestRejected
                            ? ($"VLMS: the consent form for {student.Name} needs changes",
                                $"The consent form for {student.Name} was not approved and is waiting for you to update and resubmit it.")
                            : ($"VLMS: {student.Name} needs a consent form",
                                $"{student.Name} has no consent in force, so lessons cannot be recorded. Please complete the consent form.");
                    }
                }
                else
                {
                    var daysLeft = inForce.ExpiryDate.DayNumber - today.DayNumber;
                    if (ReminderDaysBeforeExpiry.Contains(daysLeft))
                    {
                        message = ($"VLMS: {student.Name}'s consent expires on {inForce.ExpiryDate:dd/MM/yyyy}",
                            $"The annual consent for {student.Name} expires on {inForce.ExpiryDate:dd/MM/yyyy}. " +
                            "Please renew it before then: lessons cannot be recorded for a student without current consent.");
                    }
                }

                if (message is { } m)
                {
                    outcomes.AddRange(await SendToGuardiansAsync(student.Id, [m], priority, ct, ConsentPath));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Consent reminder for student {StudentId} failed; continuing with the others.", student.Id);
            }
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<NotificationOutcome>> SendToGuardiansAsync(
        int studentId, IReadOnlyList<(string Subject, string Body)> messages, NotificationPriority priority, CancellationToken ct,
        string? consentPath = null)
    {
        var studentChapterId = await _db.Students.Where(s => s.Id == studentId).Select(s => s.ChapterId).SingleAsync(ct);
        var recipients = await _db.StudentGuardianLinks
            .Where(l => l.StudentId == studentId && l.ParentGuardian!.AppUserId != null)
            .Select(l => l.ParentGuardian!.AppUser!)
            .Where(u => u.Roles.Any(r => r.Role == Role.Parent)
                && !_db.StaffStepDowns.Any(s => s.UserId == u.Id && s.ChapterId == studentChapterId && s.ReinstatedAt == null))
            .Select(u => new { u.Email, u.DisplayName })
            .Distinct()
            .ToListAsync(ct);

        var footer = _publicBaseUrl is null
            ? "\n\nVarangians"
            : consentPath is not null
                ? $"\n\nComplete or renew the consent form here: {_publicBaseUrl}{consentPath}\n\nVarangians"
                : $"\n\nSee progress and certificates on the parent dashboard: {_publicBaseUrl}/parent/dashboard\n\nVarangians";

        var outcomes = new List<NotificationOutcome>();
        foreach (var recipient in recipients)
        {
            foreach (var (subject, body) in messages)
            {
                outcomes.Add(await _notificationService.SendAsync(
                    new NotificationRequest(recipient.Email, recipient.DisplayName, subject, body + footer, priority), ct));
            }
        }

        return outcomes;
    }

    private void RequireRole(Role role)
    {
        if (!_currentUser.HasRole(role))
        {
            throw new UnauthorizedAccessException($"Caller does not hold the {role} role.");
        }
    }
}
