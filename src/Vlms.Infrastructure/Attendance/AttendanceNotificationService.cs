using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Infrastructure.Attendance;

/// <summary>
/// Tells a chapter's Safeguarding Officers that a child attended a meeting without consent in force
/// (docs/design/chapters-and-attendance.md section 3, "Consent"). One email per officer per save,
/// listing the children. <b>Content:</b> the children's names, the meeting date and a link built from
/// <c>App:PublicBaseUrl</c>; nothing from <see cref="ConsentSensitiveDetails"/>. <b>Recipients:</b>
/// users holding Safeguarding Officer in the caller's active chapter who have not left
/// (<see cref="ChapterMembership.StaffIn"/>). Never throws (cancellation aside): the register stands.
/// </summary>
public sealed class AttendanceNotificationService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AttendanceNotificationService> _logger;
    private readonly string? _publicBaseUrl;

    public AttendanceNotificationService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        INotificationService notificationService,
        ILogger<AttendanceNotificationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>Call after the register is saved, with <see cref="RegisterSaveResult.PresentWithoutConsent"/>.</summary>
    public async Task NotifyPresentWithoutConsentAsync(DateOnly meetingDate, IReadOnlyList<int> studentIds, CancellationToken ct = default)
    {
        if (studentIds.Count == 0 || _currentUser.ActiveChapterId is not int chapterId)
        {
            return;
        }

        try
        {
            var names = await _db.Students
                .Where(s => studentIds.Contains(s.Id))
                .OrderBy(s => s.Name)
                .Select(s => s.Name)
                .ToListAsync(ct);
            var officers = await ChapterMembership.StaffIn(_db, chapterId, Role.SafeguardingOfficer)
                .Select(u => new { u.Email, u.DisplayName })
                .ToListAsync(ct);

            var subject = names.Count == 1
                ? $"VLMS: {names[0]} attended without consent in force"
                : $"VLMS: {names.Count} children attended without consent in force";
            var body = $"The register for the meeting on {meetingDate:dd/MM/yyyy} marks these children present, " +
                "but none of them has an approved consent form in force:\n\n" +
                string.Join("\n", names.Select(n => $"- {n}")) +
                (_publicBaseUrl is null ? "" : $"\n\nConsent records: {_publicBaseUrl}/safeguarding/consent") +
                "\n\nVarangians";

            foreach (var officer in officers)
            {
                await _notificationService.SendAsync(
                    new NotificationRequest(officer.Email, officer.DisplayName, subject, body, NotificationPriority.SafeguardingCritical), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Attendance consent alert for {MeetingDate} failed; the register stands.", meetingDate);
        }
    }
}
