using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Progress;

/// <summary>
/// Auto-promotion (docs/design/low-level-design.md "PromotionService", STATE.md): after a
/// completion, checks whether every active <see cref="Lesson"/> in the student's
/// <see cref="Student.CurrentRankId"/> is complete; if so, closes the current
/// <see cref="StudentRankProgress"/> row, opens the next, advances
/// <see cref="Student.CurrentRankId"/>, and awards that rank's <see cref="RankBadge"/> via a new
/// <see cref="StudentBadge"/>. At the final rank (no <see cref="Rank"/> with a higher
/// <see cref="Rank.Order"/>), sets <see cref="Student.Status"/> to
/// <see cref="StudentStatus.Graduated"/> instead of advancing further.
///
/// Rank ladder ordering: <see cref="Rank.Order"/> (already on the entity, with the existing
/// <see cref="Rank.IsBefore"/> helper) — "the next rank" is the Rank with the smallest Order
/// greater than the student's current rank's Order. No schema gap here.
///
/// Precondition (not yet enforced by any student-registration flow — STATE.md Next item 1,
/// "Guardian-link creation flow ... at student registration" — is where a student's initial
/// enrolment into their first Rank is expected to open the first StudentRankProgress row): this
/// service expects an open StudentRankProgress row (CompletedAt == null) to already exist for the
/// student's current rank before promotion is checked. Throws clearly if that precondition is
/// violated, rather than silently fabricating one, so the gap is visible instead of masked.
///
/// Badge-award precondition: if no RankBadge is configured for the rank just completed, the
/// promotion still proceeds and no StudentBadge is created — RankBadge is reference data populated
/// separately (e.g. by an Admin) and its absence should not block a student's progression.
///
/// Authorization: none of its own — this is a system-internal step triggered by
/// <see cref="CompletionService.MarkCompleteAsync"/> only, after that call has already role-checked
/// the caller (Teacher). Nothing in low-level-design.md calls for an independent role gate here.
/// </summary>
public sealed class PromotionService
{
    private readonly VlmsDbContext _db;

    public PromotionService(VlmsDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns true if the student was promoted (or graduated), false if their current rank still
    /// has incomplete active lessons (the non-promotion case).
    /// </summary>
    public async Task<bool> CheckAndPromoteAsync(int studentId, CancellationToken ct = default)
    {
        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        if (student.Status != StudentStatus.Active)
        {
            return false;
        }

        var currentRank = await _db.Ranks.SingleAsync(r => r.Id == student.CurrentRankId, ct);

        var activeLessonIds = await _db.Lessons
            .Where(l => l.RankId == currentRank.Id && l.IsActive)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var completedActiveLessonCount = await _db.StudentLessonCompletions
            .Where(c => c.StudentId == studentId && !c.IsReversed && activeLessonIds.Contains(c.LessonId))
            .Select(c => c.LessonId)
            .Distinct()
            .CountAsync(ct);

        if (activeLessonIds.Count == 0 || completedActiveLessonCount < activeLessonIds.Count)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        var progress = await _db.StudentRankProgresses.SingleOrDefaultAsync(
            p => p.StudentId == studentId && p.RankId == currentRank.Id && p.CompletedAt == null, ct)
            ?? throw new InvalidOperationException(
                $"Student {studentId} has no open StudentRankProgress row for rank {currentRank.Id} " +
                "— expected one to have been opened at enrolment/rank-entry.");

        _db.Entry(progress).CurrentValues.SetValues(new StudentRankProgress
        {
            Id = progress.Id,
            StudentId = progress.StudentId,
            RankId = progress.RankId,
            StartedAt = progress.StartedAt,
            CompletedAt = now
        });

        await AwardBadgeAsync(studentId, currentRank.Id, now, ct);

        var nextRank = await _db.Ranks
            .Where(r => r.Order > currentRank.Order)
            .OrderBy(r => r.Order)
            .FirstOrDefaultAsync(ct);

        if (nextRank is null)
        {
            _db.Entry(student).CurrentValues.SetValues(new Student
            {
                Id = student.Id,
                Name = student.Name,
                DateOfBirth = student.DateOfBirth,
                CurrentRankId = student.CurrentRankId,
                Status = StudentStatus.Graduated,
                EnrolmentDate = student.EnrolmentDate,
                AssignedTeacherUserId = student.AssignedTeacherUserId,
                AppUserId = student.AppUserId
            });

            await _db.SaveChangesAsync(ct);
            return true;
        }

        _db.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = await NextId(_db.StudentRankProgresses, ct),
            StudentId = studentId,
            RankId = nextRank.Id,
            StartedAt = now,
            CompletedAt = null
        });

        _db.Entry(student).CurrentValues.SetValues(new Student
        {
            Id = student.Id,
            Name = student.Name,
            DateOfBirth = student.DateOfBirth,
            CurrentRankId = nextRank.Id,
            Status = student.Status,
            EnrolmentDate = student.EnrolmentDate,
            AssignedTeacherUserId = student.AssignedTeacherUserId,
            AppUserId = student.AppUserId
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task AwardBadgeAsync(int studentId, int completedRankId, DateTime awardedAt, CancellationToken ct)
    {
        var badge = await _db.RankBadges.SingleOrDefaultAsync(b => b.RankId == completedRankId, ct);
        if (badge is null)
        {
            return;
        }

        _db.StudentBadges.Add(new StudentBadge
        {
            Id = await NextId(_db.StudentBadges, ct),
            StudentId = studentId,
            RankBadgeId = badge.Id,
            AwardedAt = awardedAt
        });
    }

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<StudentRankProgress> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;

    private static async Task<int> NextId(DbSet<StudentBadge> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
