using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// One flagged <see cref="ConsentRecord"/> outcome for a <see cref="Student"/>: either "expiring
/// soon" (<see cref="IsExpired"/> = false, <see cref="ExpiryDate"/> within the job's warning
/// window) or "expired"/missing (<see cref="IsExpired"/> = true — no Approved, unexpired
/// <see cref="ConsentRecord"/> exists at all for this student, the same condition
/// <c>Progress.CompletionService</c> already blocks lesson completion on).
/// <see cref="ConsentRecordId"/>/<see cref="ExpiryDate"/> are null when the student has never had
/// an Approved <see cref="ConsentRecord"/> at all.
/// </summary>
public sealed record ConsentExpiryFlag(
    int StudentId, string StudentName, int? ConsentRecordId, DateOnly? ExpiryDate, bool IsExpired);

/// <summary>
/// One flagged <see cref="DbsCheck"/> outcome for a teacher (an <see cref="AppUser"/> holding
/// <see cref="Role.Teacher"/>) — mirrors <see cref="ConsentExpiryFlag"/>'s shape.
/// <see cref="DbsCheckId"/>/<see cref="ExpiryDate"/> are null when the teacher has never had a
/// Clear <see cref="DbsCheck"/> at all, or their most recent one is Flagged/Pending — treated the
/// same as "expired" (<see cref="IsExpired"/> = true), since neither state is a currently valid
/// clearance.
/// </summary>
public sealed record DbsExpiryFlag(
    int TeacherUserId, string TeacherName, int? DbsCheckId, DateOnly? ExpiryDate, bool IsExpired);

/// <summary>
/// A <see cref="Student"/> with no non-reversed <see cref="StudentLessonCompletion"/> in the last
/// <see cref="ConsentExpiryJob.AtRiskThresholdDays"/> days (functional.md: "no lesson completions
/// within 8 weeks", quality/test-plan.md TC-010). <see cref="LastActivityAt"/> is the student's
/// <see cref="Student.EnrolmentDate"/> if they have never had a completion at all.
/// </summary>
public sealed record AtRiskStudentFlag(
    int StudentId, string StudentName, DateTime LastActivityAt, int DaysSinceLastActivity);

public sealed record ConsentExpiryJobResult(
    IReadOnlyList<ConsentExpiryFlag> ConsentFlags,
    IReadOnlyList<DbsExpiryFlag> DbsFlags,
    IReadOnlyList<AtRiskStudentFlag> AtRiskStudents);

/// <summary>
/// The daily sweep named in docs/design/low-level-design.md ("ConsentExpiryJob") and
/// docs/adr/0003-scheduled-jobs-webjobs.md (run as an Azure App Service WebJob, co-located with the
/// Vlms.Web App Service plan — see <c>Vlms.Jobs</c>, the console host that triggers
/// <see cref="RunAsync"/> on a schedule). This class is the testable domain logic; it has no
/// dependency on how it is hosted/triggered.
///
/// Three things, matching STATE.md's item wording:
///
/// 1. <b>Expiry blocking</b> — already fully enforced by <c>Progress.CompletionService</c>, which
/// computes "does this student currently have an Approved, unexpired ConsentRecord" fresh at
/// completion time, not from any stored flag. This job does not need to write anything for
/// blocking to keep working; it only needs to make the same population visible to Admin/
/// Safeguarding Officer ahead of time (<see cref="SweepConsentAsync"/> deliberately reuses the same
/// "most recent Approved record" concept CompletionService checks, so a flag with
/// <see cref="ConsentExpiryFlag.IsExpired"/> = true here is exactly the population currently
/// blocked).
///
/// 2. <b>At-risk/disengaged flagging at 8 weeks</b> — low-level-design.md's ConsentExpiryJob
/// paragraph explicitly folds this in ("Also runs the at-risk/disengaged student flagging: no
/// lesson completion within 8 weeks"), even though it is a different concern from consent/DBS
/// expiry (disengagement, not safeguarding-document lapse) — that bundling is the existing design
/// decision this class follows, not one invented here. See <see cref="FlagAtRiskStudentsAsync"/>.
///
/// 3. <b>Escalation</b> (documented judgement call, since <c>NotificationService</c> — STATE.md
/// Next item 1 — doesn't exist yet, so nothing in this codebase can send real email): an escalation
/// here means an elevated-severity structured log entry (<see cref="LogFlags"/>), not a sent
/// message. adr/0003 itself names Application Insights alerting as this WebJob's monitoring
/// mechanism ("WebJob failures must be monitored explicitly... since there is no separate
/// Functions-portal execution history to fall back on") — a LogError from a scheduled WebJob is
/// exactly what that alerting acts on. Once NotificationService exists, it can call
/// <see cref="RunAsync"/> and translate <see cref="ConsentExpiryJobResult"/> into real emails with
/// retry/backoff (low-level-design.md's NotificationService failure-handling paragraph) instead of,
/// or alongside, this logging.
///
/// Authorization: role-checked inside <see cref="RunAsync"/> (<see cref="RequireAdminOrSafeguardingOfficer"/>),
/// the same defense-in-depth pattern every other service in this codebase uses. In production the
/// caller is always <see cref="Security.SystemCurrentUserContext"/> (which only ever grants these
/// two roles), so this check is a structural safety net rather than something expected to ever
/// actually deny — the same role gate additionally lets this job legitimately read
/// <see cref="DbsCheck"/> through VlmsDbContext's query filter (adr/0004). <see cref="SweepDbsAsync"/>
/// materializes real <see cref="DbsCheck"/> entities (not an anonymous-type projection) specifically
/// so this read genuinely triggers the existing read-audit interceptor, exactly as any other
/// Admin/SafeguardingOfficer read would.
/// </summary>
public sealed class ConsentExpiryJob
{
    /// <summary>
    /// low-level-design.md describes the consent/DBS expiry warning window as "configurable" but
    /// names no number, and functional.md/data-design.md are silent too. 28 days (4 weeks' notice)
    /// is a documented build-time default — the constructor parameter below is what makes it
    /// genuinely configurable, per the ADR's own wording, rather than a silently hardcoded magic
    /// number. The project owner can override the default once confirmed.
    /// </summary>
    public const int DefaultExpiryWarningWindowDays = 28;

    /// <summary>
    /// functional.md ("no lesson completions within 8 weeks") and quality/test-plan.md TC-010 both
    /// name this figure explicitly, unlike the consent/DBS window — so it is a fixed constant, not
    /// a constructor parameter.
    /// </summary>
    public const int AtRiskThresholdDays = 56;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly ILogger<ConsentExpiryJob> _logger;
    private readonly int _expiryWarningWindowDays;

    public ConsentExpiryJob(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        ILogger<ConsentExpiryJob> logger,
        int expiryWarningWindowDays = DefaultExpiryWarningWindowDays)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
        _expiryWarningWindowDays = expiryWarningWindowDays;
    }

    public async Task<ConsentExpiryJobResult> RunAsync(CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;

        var consentFlags = await SweepConsentAsync(today, ct);
        var dbsFlags = await SweepDbsAsync(today, ct);
        var atRiskStudents = await FlagAtRiskStudentsAsync(now, ct);

        LogFlags(consentFlags, dbsFlags, atRiskStudents);

        return new ConsentExpiryJobResult(consentFlags, dbsFlags, atRiskStudents);
    }

    /// <summary>
    /// Scoped to Active students only — data-design.md doesn't name a scope explicitly, but a
    /// Graduated or Inactive student needs no ongoing consent-expiry monitoring (mirrors the same
    /// Active-only scoping <see cref="FlagAtRiskStudentsAsync"/> uses for disengagement). Considers
    /// each Active student's most recent Approved <see cref="ConsentRecord"/> (by
    /// <see cref="ConsentRecord.ExpiryDate"/>) — computed in memory after materializing, not via
    /// EF's GroupBy/OrderBy/First translation, since that shape is not reliably supported across
    /// providers (matches this codebase's existing "pull small reference-scale data into memory
    /// rather than fight provider translation" approach at tens-of-users scale).
    /// </summary>
    private async Task<IReadOnlyList<ConsentExpiryFlag>> SweepConsentAsync(DateOnly today, CancellationToken ct)
    {
        var warningCutoff = today.AddDays(_expiryWarningWindowDays);

        var activeStudents = await _db.Students
            .Where(s => s.Status == StudentStatus.Active)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        var approvedConsents = await _db.ConsentRecords
            .Where(c => c.Status == ConsentStatus.Approved)
            .Select(c => new { c.Id, c.StudentId, c.ExpiryDate })
            .ToListAsync(ct);

        var latestPerStudent = approvedConsents
            .GroupBy(c => c.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.ExpiryDate).First());

        var flags = new List<ConsentExpiryFlag>();
        foreach (var student in activeStudents)
        {
            if (!latestPerStudent.TryGetValue(student.Id, out var latest) || latest.ExpiryDate < today)
            {
                flags.Add(new ConsentExpiryFlag(student.Id, student.Name, latest?.Id, latest?.ExpiryDate, IsExpired: true));
            }
            else if (latest.ExpiryDate <= warningCutoff)
            {
                flags.Add(new ConsentExpiryFlag(student.Id, student.Name, latest.Id, latest.ExpiryDate, IsExpired: false));
            }
        }

        return flags;
    }

    /// <summary>
    /// Enumerates every <see cref="AppUser"/> holding <see cref="Role.Teacher"/> (via
    /// <see cref="UserRole"/>) and considers each teacher's most recent Clear <see cref="DbsCheck"/>
    /// (by <see cref="DbsCheck.ExpiryDate"/>) — mirrors <see cref="SweepConsentAsync"/>'s shape. A
    /// teacher with no Clear <see cref="DbsCheck"/> at all is treated as
    /// <see cref="DbsExpiryFlag.IsExpired"/> = true: FR-002/data-design.md give no separate "block"
    /// mechanism for missing/invalid DBS the way FR-003 does for consent, so this job's escalation
    /// output is the only structural signal for it.
    ///
    /// Unlike <see cref="SweepConsentAsync"/>'s anonymous-type projections, the <see cref="DbsCheck"/>
    /// query below materializes full entities (<c>.ToListAsync()</c> on the <see cref="DbsCheck"/>
    /// query itself, with the id/teacher/expiry projection and grouping done afterwards in memory).
    /// <see cref="DbsCheck"/> is one of the two whole-entity-restricted, read-audited entities
    /// (adr/0004-sensitive-data-access-control.md); <see cref="Auditing.SensitiveDataAuditInterceptor"/>
    /// only fires on <see cref="IMaterializationInterceptor"/>'s "instance of a mapped entity type
    /// materialized" hook, which an anonymous-type <c>Select</c> projection never triggers — so
    /// reading via a projection here would silently read every teacher's DBS status without ever
    /// writing a <see cref="SensitiveDataAccessLog"/> row for it. Materializing the entity itself
    /// keeps the audit trail genuine for this job's daily bulk read of the most sensitive entity in
    /// the system.
    /// </summary>
    private async Task<IReadOnlyList<DbsExpiryFlag>> SweepDbsAsync(DateOnly today, CancellationToken ct)
    {
        var warningCutoff = today.AddDays(_expiryWarningWindowDays);

        var teacherUserIds = await _db.UserRoles
            .Where(r => r.Role == Role.Teacher)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(ct);

        var teacherUsers = await _db.AppUsers
            .Where(u => teacherUserIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(ct);

        var clearChecks = (await _db.DbsChecks
            .Where(d => d.Status == DbsCheckStatus.Clear)
            .ToListAsync(ct))
            .Select(d => new { d.Id, d.TeacherUserId, d.ExpiryDate });

        var latestPerTeacher = clearChecks
            .GroupBy(d => d.TeacherUserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.ExpiryDate).First());

        var flags = new List<DbsExpiryFlag>();
        foreach (var teacher in teacherUsers)
        {
            if (!latestPerTeacher.TryGetValue(teacher.Id, out var latest) || latest.ExpiryDate < today)
            {
                flags.Add(new DbsExpiryFlag(teacher.Id, teacher.DisplayName, latest?.Id, latest?.ExpiryDate, IsExpired: true));
            }
            else if (latest.ExpiryDate <= warningCutoff)
            {
                flags.Add(new DbsExpiryFlag(teacher.Id, teacher.DisplayName, latest.Id, latest.ExpiryDate, IsExpired: false));
            }
        }

        return flags;
    }

    /// <summary>
    /// Active students only, per functional.md ("at-risk/disengaged student flagging... surfaced to
    /// Admin for proactive follow-up") — a Graduated/Inactive student has no more lessons to
    /// complete, so "disengagement" doesn't apply. <see cref="AtRiskStudentFlag.LastActivityAt"/> is
    /// the latest non-reversed <see cref="StudentLessonCompletion.CompletedAt"/>, or the student's
    /// <see cref="Student.EnrolmentDate"/> if they have none yet — so a newly enrolled student isn't
    /// flagged before they've had a fair chance to complete anything (EnrolmentDate is always more
    /// recent than "today minus 8 weeks" for anyone enrolled within the last 8 weeks).
    /// </summary>
    private async Task<IReadOnlyList<AtRiskStudentFlag>> FlagAtRiskStudentsAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-AtRiskThresholdDays);

        var activeStudents = await _db.Students
            .Where(s => s.Status == StudentStatus.Active)
            .Select(s => new { s.Id, s.Name, s.EnrolmentDate })
            .ToListAsync(ct);

        var lastCompletionByStudent = await _db.StudentLessonCompletions
            .Where(c => !c.IsReversed)
            .GroupBy(c => c.StudentId)
            .Select(g => new { StudentId = g.Key, LastCompletedAt = g.Max(c => c.CompletedAt) })
            .ToDictionaryAsync(x => x.StudentId, x => x.LastCompletedAt, ct);

        var flags = new List<AtRiskStudentFlag>();
        foreach (var student in activeStudents)
        {
            var lastActivity = lastCompletionByStudent.TryGetValue(student.Id, out var lastCompletedAt)
                ? lastCompletedAt
                : student.EnrolmentDate.ToDateTime(TimeOnly.MinValue);

            if (lastActivity < cutoff)
            {
                var daysSince = (int)(now - lastActivity).TotalDays;
                flags.Add(new AtRiskStudentFlag(student.Id, student.Name, lastActivity, daysSince));
            }
        }

        return flags;
    }

    /// <summary>
    /// See the class doc comment's "Escalation" paragraph for why logging (not email) is this
    /// increment's escalation mechanism. Expired/missing consent and DBS (safeguarding-critical,
    /// matching low-level-design.md's "escalates to Admin/Safeguarding Officer" wording) are logged
    /// at Error; approaching-threshold consent/DBS and at-risk/disengaged students (functional.md
    /// only says "surfaced to Admin for proactive follow-up", not "escalate") are logged at Warning.
    /// </summary>
    private void LogFlags(
        IReadOnlyList<ConsentExpiryFlag> consentFlags,
        IReadOnlyList<DbsExpiryFlag> dbsFlags,
        IReadOnlyList<AtRiskStudentFlag> atRiskStudents)
    {
        foreach (var flag in consentFlags)
        {
            if (flag.IsExpired)
            {
                _logger.LogError(
                    "ESCALATION: student {StudentId} ({StudentName}) has no valid consent (ConsentRecordId={ConsentRecordId}, ExpiryDate={ExpiryDate}) — lesson completion is blocked until renewed.",
                    flag.StudentId, flag.StudentName, flag.ConsentRecordId, flag.ExpiryDate);
            }
            else
            {
                _logger.LogWarning(
                    "Consent record {ConsentRecordId} for student {StudentId} ({StudentName}) expires {ExpiryDate} — within the {WindowDays}-day warning window.",
                    flag.ConsentRecordId, flag.StudentId, flag.StudentName, flag.ExpiryDate, _expiryWarningWindowDays);
            }
        }

        foreach (var flag in dbsFlags)
        {
            if (flag.IsExpired)
            {
                _logger.LogError(
                    "ESCALATION: teacher {TeacherUserId} ({TeacherName}) has no valid DBS clearance (DbsCheckId={DbsCheckId}, ExpiryDate={ExpiryDate}).",
                    flag.TeacherUserId, flag.TeacherName, flag.DbsCheckId, flag.ExpiryDate);
            }
            else
            {
                _logger.LogWarning(
                    "DBS check {DbsCheckId} for teacher {TeacherUserId} ({TeacherName}) expires {ExpiryDate} — within the {WindowDays}-day warning window.",
                    flag.DbsCheckId, flag.TeacherUserId, flag.TeacherName, flag.ExpiryDate, _expiryWarningWindowDays);
            }
        }

        foreach (var flag in atRiskStudents)
        {
            _logger.LogWarning(
                "At-risk: student {StudentId} ({StudentName}) has had no lesson completion in {DaysSinceLastActivity} days (>= {ThresholdDays}-day threshold) — surfaced to Admin for proactive follow-up.",
                flag.StudentId, flag.StudentName, flag.DaysSinceLastActivity, AtRiskThresholdDays);
        }
    }

    private void RequireAdminOrSafeguardingOfficer()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to run the consent-expiry sweep.");
        }
    }
}
