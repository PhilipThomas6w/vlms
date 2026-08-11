using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Reporting;

/// <summary>Why a <see cref="StudentDueForDeletion"/> flag was raised.</summary>
public enum StudentDeletionReason
{
    /// <summary>Anchor date derived from the latest closed <see cref="StudentRankProgress.CompletedAt"/>.</summary>
    Graduated,

    /// <summary>Anchor date is <see cref="Student.LeftAt"/>, set by <see cref="Retention.LeavingService.MarkStudentInactiveAsync"/>.</summary>
    Left
}

/// <summary>
/// A <see cref="Student"/> whose retention period has passed (UK GDPR: 3 years from
/// leaving/graduating the programme, <c>docs/governance/security-compliance.md</c>).
/// <see cref="AnchorDate"/> is the leaving/graduation date the retention clock runs from — see
/// <see cref="Reason"/> for which event it represents.
/// </summary>
public sealed record StudentDueForDeletion(
    int StudentId, string StudentName, StudentDeletionReason Reason, DateTime AnchorDate, int DaysPastRetention);

/// <summary>Why a <see cref="DbsCheckDueForDeletion"/> flag was raised.</summary>
public enum DbsCheckDeletionReason
{
    /// <summary>Anchor date is the <see cref="DbsCheck"/>'s own <see cref="DbsCheck.ExpiryDate"/>.</summary>
    Expired,

    /// <summary>
    /// Anchor date is the teacher's <see cref="AppUser.LeftAt"/> (set by
    /// <see cref="Retention.LeavingService.MarkTeacherLeftAsync"/>), because it falls earlier than
    /// this check's own <see cref="DbsCheck.ExpiryDate"/> — "the teacher leaving" can only ever bring
    /// the retention anchor forward, never push it back (see this file's class doc comment).
    /// </summary>
    TeacherLeft
}

/// <summary>
/// A <see cref="DbsCheck"/> row whose retention period has passed (6 years after expiry or the
/// teacher leaving, whichever is earlier — <c>docs/governance/security-compliance.md</c>).
/// <see cref="ExpiryDate"/> is always the check's own expiry; <see cref="AnchorDate"/> is the actual
/// date the 6-year retention clock ran from, which may be earlier (see <see cref="Reason"/>).
/// </summary>
public sealed record DbsCheckDueForDeletion(
    int DbsCheckId, int TeacherUserId, string TeacherName, DateOnly ExpiryDate,
    DbsCheckDeletionReason Reason, DateOnly AnchorDate, int DaysPastRetention);

/// <summary>
/// The retention-flagging computation behind the "records due for deletion" report
/// (<c>docs/design/data-design.md</c> "Deletion enforcement — interim decision": "manual,
/// Admin-triggered deletion once a record passes its retention period (an Admin-facing 'records due
/// for deletion' report from <c>ConsentExpiryJob</c>'s wider sweep)"; also
/// <c>docs/governance/security-compliance.md</c> "Retention"). Extracted as a shared static helper —
/// the exact same "one implementation, two callers" shape <see cref="AtRiskStudentFlagging"/>
/// established for the reporting increment — so <see cref="Safeguarding.ConsentExpiryJob"/>'s daily
/// sweep (which the docs name as this report's source) and this increment's new on-demand
/// <c>RecordsDueForDeletionService</c> share one implementation rather than two copies. Deliberately
/// has <b>no role check of its own</b>, same "shared helper, caller-owned authorization" shape as
/// <see cref="AtRiskStudentFlagging"/>/<see cref="Authorization.ParentGuardianLinkage"/> —
/// <see cref="Safeguarding.ConsentExpiryJob"/> gates on Admin-or-SafeguardingOfficer,
/// <c>RecordsDueForDeletionService</c> gates on Admin-only (see its own doc comment for why).
///
/// This is a <b>flagging-only</b> computation, matching the docs' own "manual, Admin-triggered
/// deletion" wording and this increment's explicit scope: it identifies records past retention, it
/// never deletes anything. Actual deletion stays a human/DB-level action outside this system for
/// now (data-design.md: "Full automated purge is deferred... a deliberate interim choice").
///
/// <b>The "no leaving date recorded anywhere" schema gap this file originally documented (commit
/// 73eb0f8) is now closed</b> by STATE.md's "Manual 'leaving' flag for students and teachers" item —
/// <see cref="Student.LeftAt"/> and <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/> now
/// exist, set by the new <see cref="Retention.LeavingService"/> (Admin-only, no self-service path).
/// Both methods below are now genuinely additive over what existed before that item:
///
/// - <see cref="Student"/>: Graduated students are flagged exactly as before, using the derived
///   <c>StudentRankProgress.CompletedAt</c> anchor (<see cref="StudentDeletionReason.Graduated"/>) —
///   <c>PromotionService</c> closes exactly one row in the same operation that sets Status to
///   Graduated, so this remains the reliable graduation-date source. Inactive students are now also
///   flagged, anchored on <see cref="Student.LeftAt"/> (<see cref="StudentDeletionReason.Left"/>). An
///   Inactive student with no <see cref="Student.LeftAt"/> recorded (should not occur —
///   <see cref="Retention.LeavingService.MarkStudentInactiveAsync"/> always sets both together — but
///   not a database-enforced invariant, e.g. a hypothetical future direct DB edit) is skipped rather
///   than fabricated a date, the same discipline already applied to a Graduated student with no
///   closed <see cref="StudentRankProgress"/> row at all.
/// - <see cref="DbsCheck"/>: the expiry-based trigger is unchanged
///   (<see cref="DbsCheckDeletionReason.Expired"/>). "The teacher leaving" is now implemented too:
///   for a check whose teacher has <see cref="AppUser.HasLeft"/> and a <see cref="AppUser.LeftAt"/>
///   earlier than the check's own <see cref="DbsCheck.ExpiryDate"/>, the retention anchor is
///   <see cref="AppUser.LeftAt"/> instead (<see cref="DbsCheckDeletionReason.TeacherLeft"/>) — the
///   conservative reading this file always documented: the teacher-leaving clause can only ever
///   bring the flag date forward (earlier), never push it back past the check's own expiry.
/// </summary>
public static class RetentionFlagging
{
    /// <summary>docs/governance/security-compliance.md: "Student records... retained 3 years from the student leaving/graduating".</summary>
    public const int StudentRetentionYears = 3;

    /// <summary>docs/governance/security-compliance.md: "DbsCheck records... retained 6 years after expiry or the teacher leaving".</summary>
    public const int DbsCheckRetentionYears = 6;

    /// <summary>
    /// Graduated students (anchor: latest closed <see cref="StudentRankProgress"/> row) and Inactive
    /// students (anchor: <see cref="Student.LeftAt"/>) — see this file's class doc comment. A
    /// Graduated student with no closed <see cref="StudentRankProgress"/> row, or an Inactive student
    /// with no recorded <see cref="Student.LeftAt"/>, is a data anomaly not enforced by a database
    /// constraint and is skipped rather than fabricated a date.
    /// </summary>
    public static async Task<IReadOnlyList<StudentDueForDeletion>> GetStudentsDueForDeletionAsync(
        VlmsDbContext db, DateTime now, CancellationToken ct = default)
    {
        var cutoff = now.AddYears(-StudentRetentionYears);
        var flags = new List<StudentDueForDeletion>();

        var graduatedStudents = await db.Students
            .Where(s => s.Status == StudentStatus.Graduated)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        if (graduatedStudents.Count > 0)
        {
            var graduatedAtByStudent = await db.StudentRankProgresses
                .Where(p => p.CompletedAt != null)
                .GroupBy(p => p.StudentId)
                .Select(g => new { StudentId = g.Key, GraduatedAt = g.Max(p => p.CompletedAt) })
                .ToDictionaryAsync(x => x.StudentId, x => x.GraduatedAt!.Value, ct);

            foreach (var student in graduatedStudents)
            {
                if (graduatedAtByStudent.TryGetValue(student.Id, out var graduatedAt) && graduatedAt <= cutoff)
                {
                    var daysPastRetention = (int)(now - graduatedAt.AddYears(StudentRetentionYears)).TotalDays;
                    flags.Add(new StudentDueForDeletion(
                        student.Id, student.Name, StudentDeletionReason.Graduated, graduatedAt, daysPastRetention));
                }
            }
        }

        var inactiveStudents = await db.Students
            .Where(s => s.Status == StudentStatus.Inactive)
            .Select(s => new { s.Id, s.Name, s.LeftAt })
            .ToListAsync(ct);

        foreach (var student in inactiveStudents)
        {
            if (student.LeftAt is { } leftAt && leftAt <= cutoff)
            {
                var daysPastRetention = (int)(now - leftAt.AddYears(StudentRetentionYears)).TotalDays;
                flags.Add(new StudentDueForDeletion(
                    student.Id, student.Name, StudentDeletionReason.Left, leftAt, daysPastRetention));
            }
        }

        return flags;
    }

    /// <summary>
    /// Materializes real <see cref="DbsCheck"/> entities (<c>.ToListAsync()</c> on the
    /// <see cref="DbsCheck"/> query itself, enrichment done afterwards in memory) rather than an
    /// anonymous-type projection — <see cref="DbsCheck"/> is one of the two whole-entity-restricted,
    /// read-audited entities (adr/0004-sensitive-data-access-control.md), and
    /// <see cref="Auditing.SensitiveDataAuditInterceptor"/> only fires on
    /// <see cref="IMaterializationInterceptor"/>'s "mapped entity type materialized" hook, which a
    /// projection never triggers. This is exactly the bug class a prior checker review found in
    /// <see cref="Safeguarding.ConsentExpiryJob"/>'s own DBS sweep (fixed in commit d23f6e4) — this
    /// read of <see cref="DbsCheck"/> must not repeat it.
    ///
    /// Fetches every <see cref="DbsCheck"/> row (not just those already past retention by their own
    /// <see cref="DbsCheck.ExpiryDate"/>) — evaluating "due" now requires comparing each row against
    /// its teacher's <see cref="AppUser.HasLeft"/>/<see cref="AppUser.LeftAt"/>, which a SQL-side
    /// <c>ExpiryDate &lt;= cutoff</c> filter alone cannot express. This mirrors
    /// <see cref="Safeguarding.ConsentExpiryJob.SweepDbsAsync"/>'s own existing shape (materialize a
    /// broad set, compute/filter in memory) rather than inventing a narrower query — the same
    /// "fewest moving parts" precedent, at this codebase's tens-of-rows scale. One consequence,
    /// stated rather than left implicit: every <see cref="DbsCheck"/> row now generates a
    /// <see cref="SensitiveDataAccessLog"/> entry each time this report runs, not only the rows that
    /// end up flagged — the same trade-off <see cref="Safeguarding.ConsentExpiryJob.SweepDbsAsync"/>
    /// already makes for its own Clear-only sweep, and if anything a fuller audit trail of this
    /// sensitive entity, not a thinner one.
    /// </summary>
    public static async Task<IReadOnlyList<DbsCheckDueForDeletion>> GetDbsChecksDueForDeletionAsync(
        VlmsDbContext db, DateOnly today, CancellationToken ct = default)
    {
        var allChecks = await db.DbsChecks.ToListAsync(ct);

        if (allChecks.Count == 0)
        {
            return [];
        }

        var teacherIds = allChecks.Select(d => d.TeacherUserId).Distinct().ToList();
        var teacherInfo = await db.AppUsers
            .Where(u => teacherIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.HasLeft, u.LeftAt })
            .ToDictionaryAsync(x => x.Id, ct);

        var flags = new List<DbsCheckDueForDeletion>();
        foreach (var check in allChecks.OrderBy(d => d.ExpiryDate))
        {
            var teacherName = "(unknown teacher)";
            var anchor = check.ExpiryDate;
            var reason = DbsCheckDeletionReason.Expired;

            if (teacherInfo.TryGetValue(check.TeacherUserId, out var teacher))
            {
                teacherName = teacher.DisplayName;

                if (teacher.HasLeft && teacher.LeftAt is { } leftAt)
                {
                    var leftDate = DateOnly.FromDateTime(leftAt);
                    if (leftDate < anchor)
                    {
                        anchor = leftDate;
                        reason = DbsCheckDeletionReason.TeacherLeft;
                    }
                }
            }

            var retentionCutoffDate = anchor.AddYears(DbsCheckRetentionYears);
            if (retentionCutoffDate <= today)
            {
                var daysPastRetention = today.DayNumber - retentionCutoffDate.DayNumber;
                flags.Add(new DbsCheckDueForDeletion(
                    check.Id, check.TeacherUserId, teacherName, check.ExpiryDate, reason, anchor, daysPastRetention));
            }
        }

        return flags;
    }
}
