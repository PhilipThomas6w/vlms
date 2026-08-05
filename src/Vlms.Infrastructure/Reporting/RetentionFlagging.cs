using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Reporting;

/// <summary>
/// A <see cref="Student"/> whose retention period has passed (UK GDPR: 3 years from
/// leaving/graduating the programme, <c>docs/governance/security-compliance.md</c>). See
/// <see cref="RetentionFlagging"/>'s class doc comment for why only Graduated students can be
/// flagged in this increment.
/// </summary>
public sealed record StudentDueForDeletion(
    int StudentId, string StudentName, DateTime GraduatedAt, int DaysPastRetention);

/// <summary>
/// A <see cref="DbsCheck"/> row whose retention period has passed (6 years after
/// <see cref="DbsCheck.ExpiryDate"/>, <c>docs/governance/security-compliance.md</c>). See
/// <see cref="RetentionFlagging"/>'s class doc comment for why only the expiry-based trigger is
/// implemented here, not "or the teacher leaving".
/// </summary>
public sealed record DbsCheckDueForDeletion(
    int DbsCheckId, int TeacherUserId, string TeacherName, DateOnly ExpiryDate, int DaysPastRetention);

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
/// <b>Genuine schema gap found and documented, not silently patched (the load-bearing judgement
/// call in this file):</b> retention for both entity kinds is defined relative to an event date that
/// the schema does not actually record for every reachable case:
///
/// - <see cref="Student"/>: "3 years from leaving/graduating." <see cref="Student.Status"/> can be
///   Active, Inactive, or Graduated, but nothing in this codebase's write paths ever sets Status to
///   Inactive (grepped confirmed — the only writers of <see cref="Student.Status"/> are
///   student-registration, which always creates Active, and <c>Progress.PromotionService</c>, which
///   only ever sets Graduated), and there is no "left the programme on this date" field anywhere on
///   <see cref="Student"/> or elsewhere. "Graduating" <i>is</i> reliably derivable without a schema
///   change: <c>Progress.PromotionService</c> closes exactly one <see cref="StudentRankProgress"/>
///   row (sets <see cref="StudentRankProgress.CompletedAt"/>) in the same operation that sets
///   Status to Graduated — for the final rank, no new row is opened — so the latest
///   <see cref="StudentRankProgress.CompletedAt"/> across a Graduated student's history <i>is</i>
///   their graduation date. "Leaving" has no equivalent signal at all. Rather than fabricate a proxy
///   (e.g. <see cref="Student.EnrolmentDate"/> would be actively wrong — it has nothing to do with
///   when someone left — and there is no "last touched" timestamp on <see cref="Student"/> to fall
///   back on either), <see cref="GetStudentsDueForDeletionAsync"/> only ever flags Graduated
///   students. An Inactive student (should that status ever be set — it is reachable in the data
///   model, just not by any code path in this repo today, e.g. a future direct support/DB action)
///   is silently excluded from this report, not fabricated a wrong date. Tracked as a new STATE.md
///   Next item: a real "leaving date" field (plus whatever marks a student Inactive in the first
///   place) is a genuine prerequisite this report cannot itself invent.
/// - <see cref="DbsCheck"/>: "6 years after expiry or the teacher leaving." <see cref="DbsCheck.ExpiryDate"/>
///   is always present, so the expiry-based trigger is fully implementable. "The teacher leaving" has
///   no schema support at all — <see cref="AppUser"/>/<see cref="UserRole"/> carry no status/leaving
///   date of any kind, so there is no way to flag a teacher's DBS record early just because they left
///   before their most recent check expired. <see cref="GetDbsChecksDueForDeletionAsync"/>
///   implements only the expiry-based trigger; this is the conservative reading (a DBS record is
///   never flagged <i>later</i> than 6 years past its own expiry regardless of whether the teacher
///   left earlier — the "or the teacher leaving" clause could only ever bring the flag date forward,
///   never push it back), and is the same STATE.md Next item as above.
/// </summary>
public static class RetentionFlagging
{
    /// <summary>docs/governance/security-compliance.md: "Student records... retained 3 years from the student leaving/graduating".</summary>
    public const int StudentRetentionYears = 3;

    /// <summary>docs/governance/security-compliance.md: "DbsCheck records... retained 6 years after expiry or the teacher leaving".</summary>
    public const int DbsCheckRetentionYears = 6;

    /// <summary>
    /// Graduated students only — see this file's class doc comment for why Inactive students cannot
    /// be flagged without a schema change this item does not make. A Graduated student with no
    /// closed <see cref="StudentRankProgress"/> row at all (a data anomaly — should never happen
    /// given <c>Progress.PromotionService</c>'s own invariant, but not enforced by a database
    /// constraint) is likewise skipped rather than fabricated a date.
    /// </summary>
    public static async Task<IReadOnlyList<StudentDueForDeletion>> GetStudentsDueForDeletionAsync(
        VlmsDbContext db, DateTime now, CancellationToken ct = default)
    {
        var cutoff = now.AddYears(-StudentRetentionYears);

        var graduatedStudents = await db.Students
            .Where(s => s.Status == StudentStatus.Graduated)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        if (graduatedStudents.Count == 0)
        {
            return [];
        }

        var graduatedAtByStudent = await db.StudentRankProgresses
            .Where(p => p.CompletedAt != null)
            .GroupBy(p => p.StudentId)
            .Select(g => new { StudentId = g.Key, GraduatedAt = g.Max(p => p.CompletedAt) })
            .ToDictionaryAsync(x => x.StudentId, x => x.GraduatedAt!.Value, ct);

        var flags = new List<StudentDueForDeletion>();
        foreach (var student in graduatedStudents)
        {
            if (graduatedAtByStudent.TryGetValue(student.Id, out var graduatedAt) && graduatedAt <= cutoff)
            {
                var daysPastRetention = (int)(now - graduatedAt.AddYears(StudentRetentionYears)).TotalDays;
                flags.Add(new StudentDueForDeletion(student.Id, student.Name, graduatedAt, daysPastRetention));
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
    /// new read of <see cref="DbsCheck"/> must not repeat it.
    /// </summary>
    public static async Task<IReadOnlyList<DbsCheckDueForDeletion>> GetDbsChecksDueForDeletionAsync(
        VlmsDbContext db, DateOnly today, CancellationToken ct = default)
    {
        var cutoffExpiry = today.AddYears(-DbsCheckRetentionYears);

        var dueChecks = await db.DbsChecks
            .Where(d => d.ExpiryDate <= cutoffExpiry)
            .ToListAsync(ct);

        if (dueChecks.Count == 0)
        {
            return [];
        }

        var teacherIds = dueChecks.Select(d => d.TeacherUserId).Distinct().ToList();
        var teacherNames = await db.AppUsers
            .Where(u => teacherIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

        return dueChecks
            .OrderBy(d => d.ExpiryDate)
            .Select(d => new DbsCheckDueForDeletion(
                d.Id,
                d.TeacherUserId,
                teacherNames.GetValueOrDefault(d.TeacherUserId, "(unknown teacher)"),
                d.ExpiryDate,
                today.DayNumber - d.ExpiryDate.AddYears(DbsCheckRetentionYears).DayNumber))
            .ToList();
    }
}
