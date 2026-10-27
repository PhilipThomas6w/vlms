using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Reporting;

namespace Vlms.Infrastructure.Retention;

/// <summary>
/// The monthly reminder (docs/adr/0014-retention-purge.md decision 5): while any of a chapter's
/// records are due for deletion, each of that chapter's Admins gets one email a month saying how
/// many, so deletion does not depend on someone opening the report. Run by <c>Vlms.Jobs</c> as the
/// system principal (it reads every chapter); a chapter hears only about its own records, and the
/// email carries counts only, never a name.
/// </summary>
public sealed class RetentionReminderService
{
    public const string Subject = "Records due for deletion";

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly INotificationService _notificationService;
    private readonly string? _publicBaseUrl;

    public RetentionReminderService(
        VlmsDbContext db, ICurrentUserContext currentUser, INotificationService notificationService, string? publicBaseUrl = null)
    {
        _db = db;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<NotificationOutcome>> SendAsync(CancellationToken ct = default)
    {
        // The scheduled job's system principal only: it must see every chapter's records.
        if (!_currentUser.SeesAllChapters || !_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Only the scheduled job sends the retention reminder.");
        }

        var now = DateTime.UtcNow;
        var counts = new Dictionary<int, int>();
        void Count(int chapterId) => counts[chapterId] = counts.GetValueOrDefault(chapterId) + 1;

        foreach (var s in await RetentionFlagging.GetStudentsDueForDeletionAsync(_db, now, ct)) Count(s.ChapterId);
        foreach (var d in await RetentionFlagging.GetDbsChecksDueForDeletionAsync(_db, DateOnly.FromDateTime(now), ct)) Count(d.ChapterId);
        foreach (var a in await RetentionFlagging.GetApplicationsDueForDeletionAsync(_db, now, ct)) Count(a.ChapterId);

        var outcomes = new List<NotificationOutcome>();
        foreach (var (chapterId, count) in counts.OrderBy(c => c.Key))
        {
            var body = count == 1
                ? "1 record is due for deletion."
                : $"{count} records are due for deletion.";
            if (_publicBaseUrl is not null)
            {
                body += $"\n\n{_publicBaseUrl}/reporting/records-due-for-deletion";
            }

            var recipients = await ChapterMembership.StaffIn(_db, chapterId, Role.Admin)
                .OrderBy(u => u.Id)
                .Select(u => new { u.Email, u.DisplayName })
                .ToListAsync(ct);

            foreach (var recipient in recipients)
            {
                outcomes.Add(await _notificationService.SendAsync(
                    new NotificationRequest(recipient.Email, recipient.DisplayName, Subject, body, NotificationPriority.Standard), ct));
            }
        }

        return outcomes;
    }
}
