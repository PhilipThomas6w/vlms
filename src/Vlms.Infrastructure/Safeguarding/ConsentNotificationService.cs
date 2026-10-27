using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// Emails around the consent form (functional.md FR-003, adr/0008 decision 3): when a form is
/// submitted, every active Admin and Safeguarding Officer is told one is waiting and the submitting
/// parent gets a confirmation; when it is decided, the parent is told the outcome.
///
/// <b>Content.</b> Student name, the fact a form is waiting, the expiry date and a link built from
/// <c>App:PublicBaseUrl</c> only. Nothing from <see cref="ConsentSensitiveDetails"/> is read or
/// sent (adr/0004, adr/0006). The reviewer's rejection comment is not emailed (adr/0008 decision 8):
/// the rejection email says the parent must sign in to read it, and the comment is shown on their
/// consent page.
///
/// <b>Recipients.</b> Staff: users holding Admin or SafeguardingOfficer in the student's chapter
/// (FR-021, docs/adr/0010-chapters.md) who have not left (the same rule as
/// <see cref="ConsentExpiryNotifier"/>, plus the left check). Parent: the
/// <see cref="ParentGuardian"/> on the record, only if their account holds Parent in that chapter and has not left;
/// a paper form recorded by staff for a guardian with no account sends the parent nothing.
///
/// <b>Failure isolation.</b> Callers invoke these after their save. Both methods never throw
/// (cancellation aside): a failure is logged and the submission or decision stands.
/// <see cref="INotificationService"/> itself never throws.
/// </summary>
public sealed class ConsentNotificationService
{
    private readonly VlmsDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly ILogger<ConsentNotificationService> _logger;
    private readonly string? _publicBaseUrl;

    public ConsentNotificationService(
        VlmsDbContext db,
        INotificationService notificationService,
        ILogger<ConsentNotificationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _notificationService = notificationService;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>Alerts Admins and Safeguarding Officers, and confirms to the parent. Call after the save.</summary>
    public async Task NotifySubmittedAsync(int consentRecordId, CancellationToken ct = default)
    {
        try
        {
            var info = await LoadAsync(consentRecordId, ct);

            var staff = await ChapterMembership.StaffIn(_db, info.ChapterId, Role.Admin, Role.SafeguardingOfficer)
                .Select(u => new Recipient(u.Email, u.DisplayName))
                .ToListAsync(ct);
            var staffBody = $"A consent form for {info.StudentName} has been submitted and is waiting for review."
                + Link("Review it here", "/safeguarding/consent");
            foreach (var recipient in staff)
            {
                await SendAsync(recipient, $"VLMS: consent form waiting for review ({info.StudentName})", staffBody, ct);
            }

            if (info.Parent is { } parent)
            {
                await SendAsync(parent, $"VLMS: consent form received for {info.StudentName}",
                    $"Thank you. We have received the consent form for {info.StudentName}. " +
                    "A member of staff will review it and you will receive another email with the outcome.", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Consent submission emails for record {ConsentRecordId} failed; the submission stands.", consentRecordId);
        }
    }

    /// <summary>Tells the parent the outcome. Call after the decision is saved.</summary>
    public async Task NotifyDecidedAsync(int consentRecordId, CancellationToken ct = default)
    {
        try
        {
            var info = await LoadAsync(consentRecordId, ct);
            if (info.Parent is not { } parent)
            {
                return;
            }

            if (info.Status == ConsentStatus.Approved)
            {
                await SendAsync(parent, $"VLMS: consent approved for {info.StudentName}",
                    $"The consent form for {info.StudentName} has been approved. Consent is in force until {info.ExpiryDate:dd/MM/yyyy}.", ct);
            }
            else if (info.Status == ConsentStatus.Rejected)
            {
                await SendAsync(parent, $"VLMS: consent form for {info.StudentName} needs changes",
                    $"The consent form for {info.StudentName} needs changes before it can be approved.\n\n" +
                    "Please sign in to read the comments from the reviewer." +
                    Link("Please update and resubmit the form here", "/parent/consent"), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Consent decision email for record {ConsentRecordId} failed; the decision stands.", consentRecordId);
        }
    }

    private sealed record Recipient(string Email, string DisplayName);

    private sealed record RecordInfo(
        string StudentName, int ChapterId, ConsentStatus Status, DateOnly ExpiryDate, Recipient? Parent);

    private async Task<RecordInfo> LoadAsync(int consentRecordId, CancellationToken ct)
    {
        var row = await _db.ConsentRecords
            .Where(c => c.Id == consentRecordId)
            .Select(c => new
            {
                StudentName = c.Student!.Name,
                c.Status,
                c.ExpiryDate,
                c.SubmittedByParentId,
                c.ChapterId
            })
            .SingleAsync(ct);

        var parent = await _db.ParentGuardians
            .Where(p => p.Id == row.SubmittedByParentId && p.AppUserId != null)
            .Select(p => p.AppUser!)
            // A staff leaver who is also a parent is still a parent (FR-017, LeftStaffPolicy).
            .Where(u => (!u.HasLeft || u.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer)) && u.Roles.Any(r => r.Role == Role.Parent && r.ChapterId == row.ChapterId))
            .Select(u => new Recipient(u.Email, u.DisplayName))
            .FirstOrDefaultAsync(ct);

        return new RecordInfo(row.StudentName, row.ChapterId, row.Status, row.ExpiryDate, parent);
    }

    private string Link(string text, string path) =>
        _publicBaseUrl is null ? "" : $"\n\n{text}: {_publicBaseUrl}{path}";

    private async Task SendAsync(Recipient recipient, string subject, string body, CancellationToken ct) =>
        await _notificationService.SendAsync(
            new NotificationRequest(recipient.Email, recipient.DisplayName, subject, body + "\n\nVarangians", NotificationPriority.Standard), ct);
}
