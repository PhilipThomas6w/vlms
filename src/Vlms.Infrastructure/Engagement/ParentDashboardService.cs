using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Attendance;
using Vlms.Infrastructure.Badges;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Infrastructure.Engagement;

/// <summary>One rank badge awarded to a student, for display on the parent dashboard.</summary>
public sealed record ParentDashboardBadge(string RankName, DateTime AwardedAt, int RankId, bool HasImage, int RankOrder = 0);

/// <summary>One certificate earned by a student, for display on the parent dashboard.</summary>
public sealed record ParentDashboardCertificate(int CertificateId, string LessonTitle, DateTime GeneratedAt, string BlobKey);

/// <summary>
/// One linked student's summary, per functional.md's "Parent engagement" wording ("view own child's
/// progress, certificates, badges"). <see cref="ConsentStatus"/>/<see cref="ConsentExpiryDate"/> are
/// null if the student has no <see cref="ConsentRecord"/> at all yet.
/// </summary>
public sealed record ParentDashboardStudent(
    int StudentId,
    string StudentName,
    string CurrentRankName,
    DateTime? CurrentRankStartedAt,
    IReadOnlyList<ParentDashboardBadge> Badges,
    IReadOnlyList<ParentDashboardCertificate> Certificates,
    ConsentStatus? ConsentStatus,
    DateOnly? ConsentExpiryDate,
    int CurrentRankId = 0,
    int CurrentRankOrder = 0,
    bool CurrentRankHasBadgeImage = false);

/// <summary>
/// Builds the Parent dashboard (STATE.md, functional.md "Parent engagement": "Parents get... an
/// in-app dashboard (view own child's progress, certificates, badges)"). Scope decision: this is
/// deliberately the whole of what's asked here — progress (current rank + when they started it),
/// badges, certificates, and non-sensitive consent status/expiry (the same <see cref="ConsentRecord"/>
/// fields Teacher/Approver can already see; <see cref="ConsentSensitiveDetails"/>/<see cref="DbsCheck"/>
/// are not surfaced here at all — a parent views and updates their own children's consent details on
/// the consent page instead (<c>Safeguarding.ParentConsentService</c>, adr/0006), and
/// <see cref="DbsCheck"/> stays Admin/SafeguardingOfficer only — adr/0004). No reporting/analytics, no
/// notification-preferences UI: not asked for, and a minimal, correct dashboard beats a speculative
/// one (per this increment's own instructions).
///
/// Role-checked inside <see cref="GetDashboardAsync"/> itself (defense in depth, same pattern as
/// every other service in this codebase). Scoping to only the caller's own linked students reuses
/// <see cref="ParentGuardianLinkage"/> — the same relationship
/// <see cref="ParentStudentAccessHandler"/> already encodes for the single-resource case (`docs/design/
/// low-level-design.md` "Authorization model": "A Parent may only view/act on Student records linked
/// to them via StudentGuardianLink") — so there is exactly one place this join is written, not two.
///
/// All subsequent per-student data (badges, certificates, consent) is fetched via simple int-keyed
/// EF joins projected to anonymous types and combined in memory, the same style
/// <c>ConsentExpiryJob.SweepConsentAsync</c>/<c>SweepDbsAsync</c> already use — avoids
/// Include/ThenInclude chains through nullable navigation properties, and keeps every join
/// translatable regardless of provider.
///
/// **Decision reversed by Philip (STATE.md, 2026-07-20):** the certificate join filters out
/// certificates whose underlying <see cref="StudentLessonCompletion.IsReversed"/> is true — see the
/// comment on that join below for the full reasoning. This supersedes an earlier "no gap found"
/// conclusion (commit 922cc69) that deliberately left reversed completions' certificates visible.
/// </summary>
public sealed class ParentDashboardService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly IBlobStorage _blobs;

    public ParentDashboardService(VlmsDbContext db, ICurrentUserContext currentUser, IBlobStorage blobs)
    {
        _db = db;
        _currentUser = currentUser;
        _blobs = blobs;
    }

    public async Task<IReadOnlyList<ParentDashboardStudent>> GetDashboardAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Parent))
        {
            throw new UnauthorizedAccessException("Caller must hold the Parent role to view the parent dashboard.");
        }

        if (_currentUser.UserId is not int userId)
        {
            return [];
        }

        var studentIds = await ParentGuardianLinkage.LinkedStudentIds(_db, userId).ToListAsync(ct);
        if (studentIds.Count == 0)
        {
            return [];
        }

        var students = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .Join(_db.Ranks, s => s.CurrentRankId, r => r.Id, (s, r) => new { s.Id, s.Name, CurrentRankName = r.Name, CurrentRankId = r.Id, CurrentRankOrder = r.Order })
            .ToListAsync(ct);

        var openProgressByStudent = await _db.StudentRankProgresses
            .Where(p => studentIds.Contains(p.StudentId) && p.CompletedAt == null)
            .Select(p => new { p.StudentId, p.StartedAt })
            .ToDictionaryAsync(p => p.StudentId, p => p.StartedAt, ct);

        var badges = await _db.StudentBadges
            .Where(b => studentIds.Contains(b.StudentId))
            .Join(_db.RankBadges, b => b.RankBadgeId, rb => rb.Id, (b, rb) => new { b.StudentId, b.AwardedAt, rb.RankId })
            .Join(_db.Ranks, x => x.RankId, r => r.Id, (x, r) => new { x.StudentId, x.AwardedAt, x.RankId, RankName = r.Name, RankOrder = r.Order })
            .ToListAsync(ct);

        // Filters out certificates whose underlying StudentLessonCompletion has IsReversed == true.
        // Decision made by Philip, 2026-07-20 (STATE.md), superseding an earlier "no gap found"
        // conclusion (commit 922cc69) that had deliberately left these visible on the reasoning that
        // PromotionService's precedent treats "reversed" as "doesn't count toward progression", not
        // "never happened". That conclusion is now judged wrong: a reversal is a Teacher self-correction
        // meaning "this completion shouldn't have happened" (functional.md), and a certificate generated
        // from a completion that shouldn't have happened is itself wrong to show a parent — the
        // certificate's validity is derivative of the completion it was generated from, not independent
        // of it. This is the second flip on this exact question (see the design-gap history in
        // openwiki/parent-dashboard.md) — do not silently revert it again without a fresh, deliberate
        // decision recorded the same way this one was.
        var certificates = await _db.Certificates
            .Join(_db.StudentLessonCompletions, c => c.StudentLessonCompletionId, slc => slc.Id,
                (c, slc) => new { CertificateId = c.Id, c.GeneratedAt, c.BlobKey, slc.StudentId, slc.LessonId, slc.IsReversed })
            .Where(x => studentIds.Contains(x.StudentId) && !x.IsReversed)
            .Join(_db.Lessons, x => x.LessonId, l => l.Id, (x, l) => new { x.StudentId, x.CertificateId, x.GeneratedAt, x.BlobKey, LessonTitle = l.Title })
            .ToListAsync(ct);

        // Whether the current rank's badge image has been uploaded (the same RankBadge rows the badges above use).
        var badgeRankIds = await RankBadgeService.RankIdsWithImageAsync(_db, _blobs, ct);

        var consents = await _db.ConsentRecords
            .Where(c => studentIds.Contains(c.StudentId))
            .Select(c => new { c.StudentId, c.Status, c.ExpiryDate })
            .ToListAsync(ct);

        var result = new List<ParentDashboardStudent>();
        foreach (var student in students.OrderBy(s => s.Name))
        {
            var studentBadges = badges
                .Where(b => b.StudentId == student.Id)
                .Select(b => new ParentDashboardBadge(b.RankName, b.AwardedAt, b.RankId, badgeRankIds.Contains(b.RankId), b.RankOrder))
                .ToList();

            var studentCertificates = certificates
                .Where(c => c.StudentId == student.Id)
                .Select(c => new ParentDashboardCertificate(c.CertificateId, c.LessonTitle, c.GeneratedAt, c.BlobKey))
                .ToList();

            var latestConsent = consents
                .Where(c => c.StudentId == student.Id)
                .OrderByDescending(c => c.ExpiryDate)
                .FirstOrDefault();

            // Dictionary<int, DateTime> (non-nullable value) can't represent "no open row" as
            // null via GetValueOrDefault (that would collide with default(DateTime)) — TryGetValue
            // distinguishes "missing" from "present" correctly. A Graduated student legitimately has
            // no open StudentRankProgress row (PromotionService closes the last one without opening
            // a next), so this must genuinely be nullable, not defaulted to DateTime.MinValue.
            DateTime? rankStartedAt = openProgressByStudent.TryGetValue(student.Id, out var startedAt) ? startedAt : null;

            result.Add(new ParentDashboardStudent(
                student.Id,
                student.Name,
                student.CurrentRankName,
                rankStartedAt,
                studentBadges,
                studentCertificates,
                latestConsent?.Status,
                latestConsent?.ExpiryDate,
                student.CurrentRankId,
                student.CurrentRankOrder,
                badgeRankIds.Contains(student.CurrentRankId)));
        }

        return result;
    }

    /// <summary>
    /// Each linked student's journey through every rank (the dashboard's "Through the ranks"), keyed by
    /// student id. Same caller checks and linked-students scope as <see cref="GetDashboardAsync"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, StudentJourney>> GetJourneysAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Parent))
        {
            throw new UnauthorizedAccessException("Caller must hold the Parent role to view the parent dashboard.");
        }

        if (_currentUser.UserId is not int userId)
        {
            return new Dictionary<int, StudentJourney>();
        }

        var studentIds = await ParentGuardianLinkage.LinkedStudentIds(_db, userId).ToListAsync(ct);
        var journeys = new Dictionary<int, StudentJourney>();
        var badgeImageRankIds = await RankBadgeService.RankIdsWithImageAsync(_db, _blobs, ct);
        foreach (var studentId in studentIds.Distinct())
        {
            if (await StudentJourney.LoadAsync(_db, badgeImageRankIds, studentId, ct) is { } journey)
            {
                journeys[studentId] = journey;
            }
        }

        return journeys;
    }

    /// <summary>
    /// Each linked student's attendance over the last <see cref="AttendanceService.HistoryMonths"/>
    /// months (FR-020: "parents see their own child's attendance"), keyed by student id. Same caller
    /// checks and linked-students scope as <see cref="GetDashboardAsync"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, AttendanceHistory>> GetAttendanceAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Parent))
        {
            throw new UnauthorizedAccessException("Caller must hold the Parent role to view the parent dashboard.");
        }

        if (_currentUser.UserId is not int userId)
        {
            return new Dictionary<int, AttendanceHistory>();
        }

        var studentIds = await ParentGuardianLinkage.LinkedStudentIds(_db, userId).ToListAsync(ct);
        var histories = new Dictionary<int, AttendanceHistory>();
        foreach (var studentId in studentIds.Distinct())
        {
            histories[studentId] = await AttendanceService.LoadHistoryAsync(_db, studentId, DateTime.UtcNow, ct);
        }

        return histories;
    }
}
