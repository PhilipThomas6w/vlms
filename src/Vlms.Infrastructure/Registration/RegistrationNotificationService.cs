using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;

namespace Vlms.Infrastructure.Registration;

/// <summary>
/// Emails around a parent's registration application (functional.md FR-004 as amended 22/10/2026,
/// adr/0008 answers 3 and 4), following <see cref="Safeguarding.ConsentNotificationService"/>:
/// <list type="bullet">
/// <item>submitted or resubmitted: every active Safeguarding Officer;</item>
/// <item>passed stage 1: every active Admin;</item>
/// <item>more information requested, rejected, approved: the applicant.</item>
/// </list>
///
/// <b>Content.</b> The child's name, what happened and a link built from <c>App:PublicBaseUrl</c>.
/// Nothing from <see cref="StudentApplicationSensitiveDetails"/> is read or sent: not the medical,
/// dietary, special educational needs or emergency details, and not the reviewer's request or
/// comments, which may themselves describe a child's needs. The parent reads those after signing in.
///
/// <b>Recipients.</b> Staff: holders of the role who have not left. Applicant: their own account, if
/// it has not left.
///
/// <b>Failure isolation.</b> Callers invoke these after their save. They never throw (cancellation
/// aside): a failure is logged and the submission or decision stands.
/// </summary>
public sealed class RegistrationNotificationService
{
    private readonly VlmsDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly ILogger<RegistrationNotificationService> _logger;
    private readonly string? _publicBaseUrl;

    public RegistrationNotificationService(
        VlmsDbContext db,
        INotificationService notificationService,
        ILogger<RegistrationNotificationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _notificationService = notificationService;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>Tells every active Safeguarding Officer that applications are waiting for review.</summary>
    public async Task NotifySubmittedAsync(IReadOnlyCollection<int> applicationIds, bool resubmitted, CancellationToken ct = default)
    {
        try
        {
            var names = await _db.StudentApplications
                .Where(a => applicationIds.Contains(a.Id))
                .OrderBy(a => a.Id)
                .Select(a => a.ChildName)
                .ToListAsync(ct);
            if (names.Count == 0)
            {
                return;
            }

            var list = string.Join(", ", names);
            var subject = resubmitted
                ? $"VLMS: registration application updated ({list})"
                : $"VLMS: new registration application ({list})";
            var body = (resubmitted
                    ? $"A parent has answered your request and resubmitted the application to register {list}. It is waiting for safeguarding review."
                    : $"A parent has applied to register {list}. The application is waiting for safeguarding review.")
                + Link("Review it here", "/registration/review");

            foreach (var recipient in await StaffAsync(Role.SafeguardingOfficer, ct))
            {
                await SendAsync(recipient, subject, body, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Registration submission emails failed; the submission stands.");
        }
    }

    /// <summary>Tells every active Admin that an application passed safeguarding review.</summary>
    public async Task NotifyPassedSafeguardingAsync(int applicationId, CancellationToken ct = default)
    {
        try
        {
            var childName = await ChildNameAsync(applicationId, ct);
            var body = $"The application to register {childName} has passed safeguarding review and is waiting for final approval."
                + Link("Approve it here", "/registration/approve");
            foreach (var recipient in await StaffAsync(Role.Admin, ct))
            {
                await SendAsync(recipient, $"VLMS: registration waiting for approval ({childName})", body, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Registration stage-1 email for application {ApplicationId} failed; the decision stands.", applicationId);
        }
    }

    /// <summary>Tells the applicant a Safeguarding Officer needs more information. The request itself is not in the email.</summary>
    public Task NotifyMoreInformationRequestedAsync(int applicationId, CancellationToken ct = default) =>
        NotifyApplicantAsync(applicationId,
            name => $"VLMS: more information needed to register {name}",
            name => $"Before we can register {name}, a member of staff needs some more information from you. "
                + "Please sign in to read the question, answer it and resubmit the application."
                + Link("Your applications", "/registration/my-applications"),
            ct);

    /// <summary>Tells the applicant the application was rejected. The comments are not in the email.</summary>
    public Task NotifyRejectedAsync(int applicationId, CancellationToken ct = default) =>
        NotifyApplicantAsync(applicationId,
            name => $"VLMS: application to register {name}",
            name => $"We are unable to accept the application to register {name}. "
                + "Please sign in to read the comments from the reviewer."
                + Link("Your applications", "/registration/my-applications"),
            ct);

    /// <summary>Tells the applicant the child is registered.</summary>
    public Task NotifyApprovedAsync(int applicationId, CancellationToken ct = default) =>
        NotifyApplicantAsync(applicationId,
            name => $"VLMS: {name} is registered",
            name => $"The application to register {name} has been approved. You can now follow {name}'s progress on your dashboard."
                + Link("Your dashboard", "/parent/dashboard"),
            ct);

    private async Task NotifyApplicantAsync(
        int applicationId, Func<string, string> subject, Func<string, string> body, CancellationToken ct)
    {
        try
        {
            var row = await _db.StudentApplications
                .Where(a => a.Id == applicationId)
                .Select(a => new { a.ChildName, a.ApplicantUser!.Email, a.ApplicantUser.DisplayName, a.ApplicantUser.HasLeft })
                .SingleAsync(ct);
            if (row.HasLeft || string.IsNullOrWhiteSpace(row.Email))
            {
                return;
            }

            await SendAsync(new Recipient(row.Email, row.DisplayName), subject(row.ChildName), body(row.ChildName), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Registration email to the applicant for application {ApplicationId} failed; the decision stands.", applicationId);
        }
    }

    private sealed record Recipient(string Email, string DisplayName);

    private Task<List<Recipient>> StaffAsync(Role role, CancellationToken ct) =>
        _db.AppUsers
            .Where(u => !u.HasLeft && u.Roles.Any(r => r.Role == role))
            .OrderBy(u => u.Id)
            .Select(u => new Recipient(u.Email, u.DisplayName))
            .ToListAsync(ct);

    private Task<string> ChildNameAsync(int applicationId, CancellationToken ct) =>
        _db.StudentApplications.Where(a => a.Id == applicationId).Select(a => a.ChildName).SingleAsync(ct);

    private string Link(string text, string path) =>
        _publicBaseUrl is null ? "" : $"\n\n{text}: {_publicBaseUrl}{path}";

    private async Task SendAsync(Recipient recipient, string subject, string body, CancellationToken ct) =>
        await _notificationService.SendAsync(
            new NotificationRequest(recipient.Email, recipient.DisplayName, subject, body + "\n\nVarangians", NotificationPriority.Standard), ct);
}
