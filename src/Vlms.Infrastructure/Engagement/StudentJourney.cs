using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Engagement;

public enum JourneyRankState
{
    Past,
    Current,
    Ahead,
}

/// <summary>One lesson on the student's journey: completed (with its certificate) or not yet.</summary>
public sealed record JourneyLesson(
    int LessonId,
    string Code,
    string Title,
    int Order,
    IReadOnlyList<LessonTag> Tags,
    DateTime? CompletedAt,
    int? CertificateId);

/// <summary>One rank on the student's journey, with its active lessons in teaching order.</summary>
public sealed record JourneyRank(
    int RankId,
    int Order,
    string Name,
    JourneyRankState State,
    IReadOnlyList<JourneyLesson> Lessons,
    DateTime? BadgeAwardedAt,
    bool HasBadgeImage)
{
    public int CompletedCount => Lessons.Count(l => l.CompletedAt is not null);
}

/// <summary>
/// The whole rank ladder from one student's point of view (the student page's "way ahead"): every rank
/// with its active lessons, which of them the student has completed, the certificate for each completed
/// lesson, and the badge for each rank earned. Reversed completions don't count and their certificates
/// are not listed, as in <see cref="StudentRosterService"/>. No sensitive data is read.
/// </summary>
public sealed record StudentJourney(
    int StudentId,
    string StudentName,
    bool Graduated,
    IReadOnlyList<JourneyRank> Ranks)
{
    public JourneyRank? Current => Ranks.FirstOrDefault(r => r.State == JourneyRankState.Current);

    /// <summary>The first lesson in the current rank, in teaching order, not yet completed.</summary>
    public JourneyLesson? NextLesson => Current?.Lessons.FirstOrDefault(l => l.CompletedAt is null);

    /// <summary>
    /// Builds a journey with no caller check of its own: every caller must authorize first (as
    /// <see cref="StudentPageService"/> does by resolving the caller's own student).
    /// </summary>
    internal static async Task<StudentJourney?> LoadAsync(VlmsDbContext db, int studentId, CancellationToken ct)
    {
        var student = await db.Students
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Id, s.Name, s.CurrentRankId, s.Status })
            .SingleOrDefaultAsync(ct);
        if (student is null)
        {
            return null;
        }

        var ranks = await db.Ranks.OrderBy(r => r.Order).Select(r => new { r.Id, r.Order, r.Name }).ToListAsync(ct);
        var lessons = await db.Lessons
            .Where(l => l.IsActive)
            .Select(l => new { l.Id, l.RankId, l.Code, l.Title, l.Order })
            .ToListAsync(ct);
        var tags = (await db.LessonTagAssignments.Select(t => new { t.LessonId, t.Tag }).ToListAsync(ct))
            .ToLookup(t => t.LessonId, t => t.Tag);
        var completions = (await db.StudentLessonCompletions
                .Where(c => c.StudentId == studentId && !c.IsReversed)
                .Select(c => new { c.Id, c.LessonId, c.CompletedAt })
                .ToListAsync(ct))
            .GroupBy(c => c.LessonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CompletedAt).First());
        var completionIds = completions.Values.Select(c => c.Id).ToList();
        var certificates = (await db.Certificates
                .Where(c => completionIds.Contains(c.StudentLessonCompletionId))
                .Select(c => new { c.Id, c.StudentLessonCompletionId })
                .ToListAsync(ct))
            .ToDictionary(c => c.StudentLessonCompletionId, c => c.Id);
        var badges = (await db.StudentBadges
                .Where(b => b.StudentId == studentId)
                .Join(db.RankBadges, b => b.RankBadgeId, rb => rb.Id, (b, rb) => new { rb.RankId, b.AwardedAt })
                .ToListAsync(ct))
            .GroupBy(b => b.RankId)
            .ToDictionary(g => g.Key, g => g.Min(b => b.AwardedAt));
        var badgeImages = (await db.RankBadges.Select(rb => rb.RankId).ToListAsync(ct)).ToHashSet();

        var graduated = student.Status == StudentStatus.Graduated;
        var currentOrder = ranks.FirstOrDefault(r => r.Id == student.CurrentRankId)?.Order ?? 0;

        var journeyRanks = ranks.Select(r => new JourneyRank(
                r.Id,
                r.Order,
                r.Name,
                graduated || r.Order < currentOrder ? JourneyRankState.Past
                    : r.Order == currentOrder ? JourneyRankState.Current
                    : JourneyRankState.Ahead,
                lessons.Where(l => l.RankId == r.Id)
                    .OrderBy(l => l.Order).ThenBy(l => l.Code)
                    .Select(l =>
                    {
                        var done = completions.GetValueOrDefault(l.Id);
                        return new JourneyLesson(
                            l.Id, l.Code, l.Title, l.Order,
                            tags[l.Id].Order().ToList(),
                            done?.CompletedAt,
                            done is not null && certificates.TryGetValue(done.Id, out var certificateId) ? certificateId : null);
                    })
                    .ToList(),
                badges.TryGetValue(r.Id, out var awardedAt) ? awardedAt : null,
                badgeImages.Contains(r.Id)))
            .ToList();

        return new StudentJourney(student.Id, student.Name, graduated, journeyRanks);
    }
}
