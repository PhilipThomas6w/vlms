using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Chapters;

/// <summary>The number of students on roll at one rank in one chapter. Counts only: no names, no ids.</summary>
public sealed record ChapterRankTotal(int RankId, string RankName, int RankOrder, int Students);

/// <summary>One chapter's anonymous totals for the national administrator.</summary>
public sealed record ChapterTotals(int ChapterId, string ChapterName, bool IsActive, int StudentsOnRoll, IReadOnlyList<ChapterRankTotal> ByRank);

/// <summary>
/// Anonymous figures per chapter for the national administrator (FR-021, docs/design/chapters-and-
/// attendance.md 2.7 and section 5, Philip 24/10/2026): how many students are on roll (status
/// Active) at each rank in each chapter, never who. The national administrator holds no chapter
/// role by virtue of that job, so the chapter query filters show them no student at all; these
/// totals are therefore read with one fixed aggregate SQL statement, which returns only a chapter
/// id, a rank id and a count per row. It is one of the few sanctioned reads past the chapter
/// filters (with <see cref="VlmsDbContext.NextIdAsync{T}"/>), named in docs/adr/0010-chapters.md;
/// <c>build/check-access-control.ps1</c> fails the gate if raw SQL appears in any other file.
///
/// Exact counts are shown, however small (assumption recorded in the ADR: no small-number
/// suppression was asked for).
/// </summary>
public sealed class NationalTotalsService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public NationalTotalsService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private sealed record Row(int ChapterId, int RankId, int Students);

    /// <summary>Every chapter with its students on roll per rank. National administrator with MFA only.</summary>
    public async Task<IReadOnlyList<ChapterTotals>> GetAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.NationalAdministrator) || !_currentUser.HasMfaEnabled)
        {
            throw new UnauthorizedAccessException(
                "Only a National Administrator with two-factor authentication turned on can see the national figures.");
        }

        var active = (int)StudentStatus.Active;
        var rows = await _db.Database
            .SqlQuery<Row>($"""
                SELECT chapter_id AS "ChapterId", current_rank_id AS "RankId", COUNT(*) AS "Students"
                FROM students
                WHERE status = {active}
                GROUP BY chapter_id, current_rank_id
                """)
            .ToListAsync(ct);

        var ranks = await _db.Ranks.OrderBy(r => r.Order).Select(r => new { r.Id, r.Name, r.Order }).ToListAsync(ct);
        var chapters = await _db.Chapters.OrderBy(c => c.Name).ToListAsync(ct);

        return chapters
            .Select(c =>
            {
                var byRank = ranks
                    .Select(r => new ChapterRankTotal(
                        r.Id, r.Name, r.Order, rows.Where(x => x.ChapterId == c.Id && x.RankId == r.Id).Sum(x => x.Students)))
                    .ToList();
                return new ChapterTotals(c.Id, c.Name, c.IsActive, byRank.Sum(r => r.Students), byRank);
            })
            .ToList();
    }
}
