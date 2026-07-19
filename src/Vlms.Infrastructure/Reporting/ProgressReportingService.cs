using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Reporting;

/// <summary>Active-student headcount currently sitting in one <see cref="Rank"/>.</summary>
public sealed record RankStudentCount(int RankId, string RankName, int ActiveStudentCount);

/// <summary>Student totals by <see cref="StudentStatus"/>.</summary>
public sealed record StudentStatusCounts(int ActiveCount, int InactiveCount, int GraduatedCount);

/// <summary>
/// One promotion event: a <see cref="StudentRankProgress"/> row that has closed
/// (<see cref="StudentRankProgress.CompletedAt"/> is set), meaning the named student completed the
/// named rank on that date (data-design.md: "closes when they complete it (auto-promotion trigger)").
/// </summary>
public sealed record PromotionHistoryEntry(int StudentId, string StudentName, string RankName, DateTime CompletedAt);

/// <summary>
/// The "core progress reports" half of this increment (functional.md "Reporting (MVP)": "Core
/// progress reports: rank/completion stats, promotion history."). Deliberately only the metrics that
/// sentence names — no invented KPIs beyond it.
/// </summary>
public sealed record ProgressStatsReport(
    IReadOnlyList<RankStudentCount> StudentsByRank,
    StudentStatusCounts StatusCounts,
    int TotalLessonCompletions,
    IReadOnlyList<PromotionHistoryEntry> PromotionHistory);

/// <summary>
/// Admin-facing reporting (STATE.md "Reporting screens: core progress stats + at-risk flagging"),
/// backing a single Blazor page — same "read-only aggregation service backing a page" shape as
/// <c>Engagement.ParentDashboardService</c>, but Admin-facing rather than Parent-facing.
///
/// <b>Role scope, a documented judgement call:</b> gated Admin-only (<see cref="RequireAdmin"/>),
/// not the Admin-or-SafeguardingOfficer pattern the consent/DBS pages use. VISION.md's mission
/// statement names the viewer explicitly — "gives the Admin core progress and at-risk reporting" —
/// and functional.md's "Reporting (MVP)" section says at-risk students are "surfaced to Admin for
/// proactive follow-up", never naming SafeguardingOfficer. That is unlike FR-001/FR-002's consent/DBS
/// data, which low-level-design.md/data-design.md explicitly scope to "Admin and Safeguarding
/// Officer" throughout. Disengagement (no lesson completions) is a distinct concern from a
/// safeguarding-document lapse, and nothing in the docs extends SafeguardingOfficer's remit to it —
/// so this uses the existing single-role <c>RequireAdmin</c> policy, not a new
/// <c>AnyRoleRequirement</c> policy.
///
/// <b>Metrics scope:</b> exactly what functional.md's "Reporting (MVP)" section names — rank/
/// completion stats (<see cref="RankStudentCount"/>, <see cref="StudentStatusCounts"/>, total
/// non-reversed lesson completions) and promotion history (<see cref="PromotionHistoryEntry"/>, one
/// row per closed <see cref="StudentRankProgress"/>). Nothing beyond that is invented.
///
/// <b>At-risk flagging</b> reuses <see cref="AtRiskStudentFlagging"/> — the exact computation
/// <c>Safeguarding.ConsentExpiryJob</c>'s daily sweep already uses for its 8-week disengagement flag
/// — rather than re-deriving the window/population here. That is deliberately the same population a
/// human would see if they read the job's Warning-level log output, just made browsable on demand
/// instead of waiting for the next scheduled run.
///
/// Touches no ADR-0004-restricted entity (<see cref="DbsCheck"/>/<see cref="ConsentSensitiveDetails"/>)
/// at all — progress/rank/completion data is not safeguarding-sensitive, so the query filter and
/// read-audit interceptor are simply irrelevant here, not bypassed.
/// </summary>
public sealed class ProgressReportingService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ProgressReportingService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ProgressStatsReport> GetProgressStatsAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var ranks = await _db.Ranks
            .OrderBy(r => r.Order)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        var activeCountsByRank = await _db.Students
            .Where(s => s.Status == StudentStatus.Active)
            .GroupBy(s => s.CurrentRankId)
            .Select(g => new { RankId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RankId, x => x.Count, ct);

        var studentsByRank = ranks
            .Select(r => new RankStudentCount(r.Id, r.Name, activeCountsByRank.GetValueOrDefault(r.Id)))
            .ToList();

        var statusCounts = new StudentStatusCounts(
            await _db.Students.CountAsync(s => s.Status == StudentStatus.Active, ct),
            await _db.Students.CountAsync(s => s.Status == StudentStatus.Inactive, ct),
            await _db.Students.CountAsync(s => s.Status == StudentStatus.Graduated, ct));

        var totalLessonCompletions = await _db.StudentLessonCompletions.CountAsync(c => !c.IsReversed, ct);

        var promotionRows = await _db.StudentRankProgresses
            .Where(p => p.CompletedAt != null)
            .Join(_db.Students, p => p.StudentId, s => s.Id,
                (p, s) => new { p.CompletedAt, StudentId = s.Id, StudentName = s.Name, p.RankId })
            .Join(_db.Ranks, x => x.RankId, r => r.Id,
                (x, r) => new { x.CompletedAt, x.StudentId, x.StudentName, RankName = r.Name })
            .ToListAsync(ct);

        var promotionHistory = promotionRows
            .OrderByDescending(x => x.CompletedAt)
            .Select(x => new PromotionHistoryEntry(x.StudentId, x.StudentName, x.RankName, x.CompletedAt!.Value))
            .ToList();

        return new ProgressStatsReport(studentsByRank, statusCounts, totalLessonCompletions, promotionHistory);
    }

    public async Task<IReadOnlyList<AtRiskStudentFlag>> GetAtRiskStudentsAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        return await AtRiskStudentFlagging.GetAtRiskStudentsAsync(_db, DateTime.UtcNow, ct);
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to view progress reporting.");
        }
    }
}
