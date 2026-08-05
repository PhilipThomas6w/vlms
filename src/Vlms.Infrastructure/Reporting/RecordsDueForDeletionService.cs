using Vlms.Domain;

namespace Vlms.Infrastructure.Reporting;

/// <summary>Combined "records due for deletion" report — see <see cref="RecordsDueForDeletionService"/>.</summary>
public sealed record RecordsDueForDeletionReport(
    IReadOnlyList<StudentDueForDeletion> Students,
    IReadOnlyList<DbsCheckDueForDeletion> DbsChecks);

/// <summary>
/// STATE.md "'Records due for deletion' report" — the interim retention-enforcement mechanism named
/// in <c>docs/design/data-design.md</c> ("Deletion enforcement — interim decision") and
/// <c>docs/governance/security-compliance.md</c> ("Retention"): a read-only, Admin-facing report
/// flagging <see cref="Student"/>/<see cref="DbsCheck"/> records past their retention
/// period, backing a single Blazor page — same "read-only aggregation service backing a page" shape
/// as <see cref="ProgressReportingService"/>.
///
/// <b>Role scope, a documented judgement call (same discipline
/// <see cref="ProgressReportingService"/>'s own doc comment applies):</b> gated Admin-only, matching
/// both docs' exact wording — data-design.md calls this "an Admin-facing... report", and every
/// retention/deletion sentence in security-compliance.md says "manual, <i>Admin</i>-triggered
/// deletion", never naming SafeguardingOfficer the way FR-001/FR-002's consent/DBS *management*
/// pages do. This is narrower than the Admin-or-SafeguardingOfficer gate
/// <c>Safeguarding.ConsentRecordService</c>/<c>DbsCheckService</c> use for ordinary DBS
/// viewing/editing — deliberately so, per the docs' own wording — and it does not weaken
/// adr/0004's whole-entity restriction: Admin is already one of the two roles that restriction
/// allows, so a narrower Admin-only page-level gate here is strictly inside what the
/// <see cref="DbsCheck"/> query filter already permits, not a bypass of it.
///
/// <b>Flagging only, per this item's own scope instruction:</b> no delete/purge action is built here
/// — data-design.md explicitly defers automated purge, and "manual, Admin-triggered deletion" reads
/// as a human process (a support/DB-level action) outside this system, the same narrow reading
/// <c>ConsentExpiryJob</c>'s "escalation" judgement call already established for a different
/// docs-silent gap (log-only, not email, until NotificationService existed). This service only
/// surfaces the list.
///
/// Delegates the actual flagging computation to <see cref="RetentionFlagging"/> — see that class's
/// doc comment for the full reasoning, including the genuine "no leaving date recorded anywhere in
/// the schema" gap this item surfaced (Inactive students and "the teacher leaving" cannot be
/// flagged today, only Graduated students and DBS-expiry can) and for why the same computation also
/// now runs inside <see cref="Safeguarding.ConsentExpiryJob"/>'s daily sweep (data-design.md sources
/// this report "from ConsentExpiryJob's wider sweep").
///
/// <see cref="DbsCheck"/> reads go through <see cref="RetentionFlagging.GetDbsChecksDueForDeletionAsync"/>,
/// which materializes real entities so the existing read-audit interceptor
/// (<see cref="Auditing.SensitiveDataAuditInterceptor"/>) genuinely fires — see that method's doc
/// comment. <see cref="Student"/> is not access-restricted, so no query-filter concern there.
/// </summary>
public sealed class RecordsDueForDeletionService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public RecordsDueForDeletionService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<RecordsDueForDeletionReport> GetReportAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var students = await RetentionFlagging.GetStudentsDueForDeletionAsync(_db, now, ct);
        var dbsChecks = await RetentionFlagging.GetDbsChecksDueForDeletionAsync(_db, today, ct);

        return new RecordsDueForDeletionReport(students, dbsChecks);
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to view the records due for deletion report.");
        }
    }
}
