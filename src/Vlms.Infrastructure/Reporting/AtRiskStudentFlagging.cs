using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Reporting;

/// <summary>
/// A <see cref="Student"/> with no non-reversed <see cref="StudentLessonCompletion"/> in the last
/// <see cref="AtRiskStudentFlagging.AtRiskThresholdDays"/> days (functional.md "Reporting": "no
/// lesson completions within 8 weeks"; quality/test-plan.md TC-010). <see cref="LastActivityAt"/>
/// is the student's <see cref="Student.EnrolmentDate"/> if they have never had a completion at all.
/// </summary>
public sealed record AtRiskStudentFlag(
    int StudentId, string StudentName, DateTime LastActivityAt, int DaysSinceLastActivity);

/// <summary>
/// The 8-week disengagement-flag computation, extracted out of
/// <see cref="Safeguarding.ConsentExpiryJob"/> (the safeguarding-consent increment first built this
/// exact query as part of its daily sweep — low-level-design.md's ConsentExpiryJob paragraph bundles
/// at-risk/disengaged flagging into that same job) so it has exactly one implementation shared by
/// both callers: the WebJob and this reporting increment's on-demand Admin screen
/// (<see cref="ProgressReportingService"/>). Per this increment's own instructions, the disengagement
/// window is reused rather than re-derived a second time.
///
/// Deliberately has <b>no role check of its own</b> — the same "shared helper, caller-owned
/// authorization" shape as <see cref="Authorization.ParentGuardianLinkage"/>. The two current callers
/// legitimately have different role scopes for the same underlying query:
/// <see cref="Safeguarding.ConsentExpiryJob"/> gates on Admin-or-SafeguardingOfficer before calling
/// this; <see cref="ProgressReportingService"/> gates on Admin-only (see its doc comment for why).
/// Baking a single role check in here would force one of those two scopes to be wrong.
/// </summary>
public static class AtRiskStudentFlagging
{
    /// <summary>
    /// functional.md ("no lesson completions within 8 weeks") and quality/test-plan.md TC-010 both
    /// name this figure explicitly, unlike the consent/DBS warning window — so it is a fixed
    /// constant, not a per-caller configurable parameter.
    /// </summary>
    public const int AtRiskThresholdDays = 56;

    /// <summary>
    /// Active students only, per functional.md ("at-risk/disengaged student flagging... surfaced to
    /// Admin for proactive follow-up") — a Graduated/Inactive student has no more lessons to
    /// complete, so "disengagement" doesn't apply. <see cref="AtRiskStudentFlag.LastActivityAt"/> is
    /// the latest non-reversed <see cref="StudentLessonCompletion.CompletedAt"/>, or the student's
    /// <see cref="Student.EnrolmentDate"/> if they have none yet — so a newly enrolled student isn't
    /// flagged before they've had a fair chance to complete anything (EnrolmentDate is always more
    /// recent than "today minus 8 weeks" for anyone enrolled within the last 8 weeks).
    /// </summary>
    public static async Task<IReadOnlyList<AtRiskStudentFlag>> GetAtRiskStudentsAsync(
        VlmsDbContext db, DateTime now, CancellationToken ct = default)
    {
        var cutoff = now.AddDays(-AtRiskThresholdDays);

        var activeStudents = await db.Students
            .Where(s => s.Status == StudentStatus.Active)
            .Select(s => new { s.Id, s.Name, s.EnrolmentDate })
            .ToListAsync(ct);

        var lastCompletionByStudent = await db.StudentLessonCompletions
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
}
