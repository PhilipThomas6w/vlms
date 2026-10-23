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
/// <b>Reminder schedule.</b> The daily <see cref="ConsentExpiryJob"/> sweep flags every student
/// in the warning window every day, so reminding on every flag would email parents daily for four
/// weeks. Reminders go only when the latest consent expires in exactly
/// <see cref="ReminderDaysBeforeExpiry"/> days, plus one <see cref="NotificationPriority.SafeguardingCritical"/>
/// notice the day after it lapses (consent is valid through its <see cref="ConsentRecord.ExpiryDate"/>).
/// No sent-reminder state is stored: a missed daily run skips that day's reminder rather than
/// adding a table. Students with no consent record at all are left to the Admin/SO digest
/// (<see cref="ConsentExpiryNotifier"/>), since there is nothing to remind the parent to renew.
/// </summary>
public sealed class GuardianNotificationService
{
    public static readonly IReadOnlyList<int> ReminderDaysBeforeExpiry = [28, 7, 1];

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
    /// Sends consent-expiry reminders to guardians from the daily sweep's flags, on the schedule in
    /// the class comment. Admin/SafeguardingOfficer only, the same gate as <see cref="ConsentExpiryNotifier"/>.
    /// </summary>
    public async Task<IReadOnlyList<NotificationOutcome>> SendConsentRemindersAsync(
        ConsentExpiryJobResult result, DateOnly today, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to send consent-expiry reminders.");
        }

        var outcomes = new List<NotificationOutcome>();
        foreach (var flag in result.ConsentFlags)
        {
            if (flag.ExpiryDate is not { } expiry)
            {
                continue;
            }

            var daysLeft = expiry.DayNumber - today.DayNumber;
            (string Subject, string Body)? message = null;
            var priority = NotificationPriority.Standard;

            if (!flag.IsExpired && ReminderDaysBeforeExpiry.Contains(daysLeft))
            {
                message = ($"VLMS: {flag.StudentName}'s consent expires on {expiry:dd/MM/yyyy}",
                    $"The annual consent for {flag.StudentName} expires on {expiry:dd/MM/yyyy}. " +
                    "Please renew it before then: lessons cannot be recorded for a student without current consent.");
            }
            else if (flag.IsExpired && daysLeft == -1)
            {
                priority = NotificationPriority.SafeguardingCritical;
                message = ($"VLMS: {flag.StudentName}'s consent has expired",
                    $"The annual consent for {flag.StudentName} expired on {expiry:dd/MM/yyyy}. " +
                    "Lessons cannot be recorded until it is renewed. Please renew it as soon as possible.");
            }

            if (message is { } m)
            {
                outcomes.AddRange(await SendToGuardiansAsync(flag.StudentId, [m], priority, ct));
            }
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<NotificationOutcome>> SendToGuardiansAsync(
        int studentId, IReadOnlyList<(string Subject, string Body)> messages, NotificationPriority priority, CancellationToken ct)
    {
        var recipients = await _db.StudentGuardianLinks
            .Where(l => l.StudentId == studentId && l.ParentGuardian!.AppUserId != null)
            .Select(l => l.ParentGuardian!.AppUser!)
            // A staff leaver who is also a parent is still a parent (FR-017, LeftStaffPolicy).
            .Where(u => (!u.HasLeft || u.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer)) && u.Roles.Any(r => r.Role == Role.Parent))
            .Select(u => new { u.Email, u.DisplayName })
            .Distinct()
            .ToListAsync(ct);

        var footer = _publicBaseUrl is null
            ? "\n\nVarangians"
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
