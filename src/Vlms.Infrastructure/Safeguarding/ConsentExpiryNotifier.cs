using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// Translates a <see cref="ConsentExpiryJob"/> sweep's <see cref="ConsentExpiryJobResult"/> into
/// real safeguarding-critical emails via <see cref="INotificationService"/> — the mechanism
/// <see cref="ConsentExpiryJob"/>'s own doc comment named ("once NotificationService exists it can
/// call RunAsync and translate the returned ConsentExpiryJobResult into real emails with retry/
/// backoff... instead of, or alongside, this logging").
///
/// <b>Judgement call: ALONGSIDE, not instead of.</b> <see cref="ConsentExpiryJob"/> keeps writing
/// its own structured log entries unchanged — adr/0003 names Application Insights alerting on
/// WebJob logs as this project's whole-system monitoring mechanism for the job generally (not just
/// this one notification path), so removing the logging would reduce observability for no reason;
/// this class is purely additive, called by the host (<c>Vlms.Jobs/Program.cs</c>) straight after
/// <see cref="ConsentExpiryJob.RunAsync"/> returns.
///
/// <b>Scope decision: only the two <c>IsExpired</c> = true flag categories are "safeguarding-
/// critical" here</b> (no valid consent, no valid DBS) — matching low-level-design.md's own wording
/// exactly ("a failed send for a safeguarding-critical notification (expired consent, expired DBS)
/// is retried with backoff..."). Approaching-window consent/DBS flags and at-risk/disengaged
/// students stay log-only in this increment (<see cref="ConsentExpiryJob.LogFlags"/> already
/// covers them at <c>LogWarning</c>) — STATE.md's item wording is specifically "retry/escalation
/// for safeguarding-critical notifications", and a routine/non-critical email path has its own
/// unresolved questions (cadence, recipient — Admin, or the affected parent/teacher themselves?)
/// that are out of scope here. <see cref="INotificationService"/>'s <see cref="NotificationPriority.Standard"/>
/// path is still fully built and tested, ready for a future increment that wires up routine
/// parent-facing emails (completion/promotion confirmations — functional.md "Parent engagement").
///
/// <b>One digest email per Admin/SafeguardingOfficer <see cref="AppUser"/>, not one email per
/// flagged student/teacher per recipient</b> — a daily sweep with N flags and M recipients sending
/// N x M separate emails would be unusable noise; a single digest matches the "daily sweep" framing
/// and low-level-design.md's "escalates to Admin/Safeguarding Officer" wording (the escalation is
/// to the role, as a whole).
///
/// Role-checked inside <see cref="NotifyAsync"/> itself (same defense-in-depth pattern every other
/// service in this codebase uses) even though, in production, the caller is always
/// <see cref="Security.SystemCurrentUserContext"/> — the same structural-safety-net reasoning
/// <see cref="ConsentExpiryJob"/>'s own <c>RequireAdminOrSafeguardingOfficer</c> documents.
/// </summary>
public sealed class ConsentExpiryNotifier
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly INotificationService _notificationService;

    public ConsentExpiryNotifier(VlmsDbContext db, ICurrentUserContext currentUser, INotificationService notificationService)
    {
        _db = db;
        _currentUser = currentUser;
        _notificationService = notificationService;
    }

    public async Task<IReadOnlyList<NotificationOutcome>> NotifyAsync(ConsentExpiryJobResult result, CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();

        var expiredConsent = result.ConsentFlags.Where(f => f.IsExpired).ToList();
        var expiredDbs = result.DbsFlags.Where(f => f.IsExpired).ToList();

        if (expiredConsent.Count == 0 && expiredDbs.Count == 0)
        {
            return [];
        }

        var subject = $"VLMS safeguarding escalation: {expiredConsent.Count + expiredDbs.Count} issue(s) require attention";
        var body = BuildDigestBody(expiredConsent, expiredDbs);

        var recipients = await _db.UserRoles
            .Where(r => r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer)
            .Select(r => r.UserId)
            .Distinct()
            .Join(_db.AppUsers, userId => userId, user => user.Id, (_, user) => new { user.Email, user.DisplayName })
            .ToListAsync(ct);

        var outcomes = new List<NotificationOutcome>();
        foreach (var recipient in recipients)
        {
            var outcome = await _notificationService.SendAsync(
                new NotificationRequest(recipient.Email, recipient.DisplayName, subject, body, NotificationPriority.SafeguardingCritical),
                ct);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    private static string BuildDigestBody(IReadOnlyList<ConsentExpiryFlag> expiredConsent, IReadOnlyList<DbsExpiryFlag> expiredDbs)
    {
        var lines = new List<string>();

        foreach (var flag in expiredConsent)
        {
            lines.Add($"- Consent: {flag.StudentName} (student #{flag.StudentId}) has no valid consent on record.");
        }

        foreach (var flag in expiredDbs)
        {
            lines.Add($"- DBS: {flag.TeacherName} (user #{flag.TeacherUserId}) has no valid DBS clearance on record.");
        }

        return string.Join("\n", lines);
    }

    private void RequireAdminOrSafeguardingOfficer()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to send consent-expiry escalation notifications.");
        }
    }
}
