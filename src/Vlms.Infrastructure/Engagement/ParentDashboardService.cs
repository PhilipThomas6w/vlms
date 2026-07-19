using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Infrastructure.Engagement;

/// <summary>One rank badge awarded to a student, for display on the parent dashboard.</summary>
public sealed record ParentDashboardBadge(string RankName, DateTime AwardedAt);

/// <summary>One certificate earned by a student, for display on the parent dashboard.</summary>
public sealed record ParentDashboardCertificate(string LessonTitle, DateTime GeneratedAt, string BlobKey);

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
    DateOnly? ConsentExpiryDate);

/// <summary>
/// Builds the Parent dashboard (STATE.md, functional.md "Parent engagement": "Parents get... an
/// in-app dashboard (view own child's progress, certificates, badges)"). Scope decision: this is
/// deliberately the whole of what's asked here — progress (current rank + when they started it),
/// badges, certificates, and non-sensitive consent status/expiry (the same <see cref="ConsentRecord"/>
/// fields Teacher/Approver can already see; <see cref="ConsentSensitiveDetails"/>/<see cref="DbsCheck"/>
/// are not surfaced at all, since those stay whole-entity-restricted to Admin/SafeguardingOfficer
/// via the existing query filter regardless of role — adr/0004). No reporting/analytics, no
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
/// **Design-gap check resolved, no gap found (STATE.md):** the certificate join deliberately does
/// not filter on <see cref="StudentLessonCompletion.IsReversed"/> — see the comment on that join
/// below for the full reasoning.
/// </summary>
public sealed class ParentDashboardService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ParentDashboardService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
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
            .Join(_db.Ranks, s => s.CurrentRankId, r => r.Id, (s, r) => new { s.Id, s.Name, CurrentRankName = r.Name })
            .ToListAsync(ct);

        var openProgressByStudent = await _db.StudentRankProgresses
            .Where(p => studentIds.Contains(p.StudentId) && p.CompletedAt == null)
            .Select(p => new { p.StudentId, p.StartedAt })
            .ToDictionaryAsync(p => p.StudentId, p => p.StartedAt, ct);

        var badges = await _db.StudentBadges
            .Where(b => studentIds.Contains(b.StudentId))
            .Join(_db.RankBadges, b => b.RankBadgeId, rb => rb.Id, (b, rb) => new { b.StudentId, b.AwardedAt, rb.RankId })
            .Join(_db.Ranks, x => x.RankId, r => r.Id, (x, r) => new { x.StudentId, x.AwardedAt, RankName = r.Name })
            .ToListAsync(ct);

        // Deliberately does NOT filter out certificates whose underlying StudentLessonCompletion has
        // IsReversed == true (checked against data-design.md/low-level-design.md/functional.md before
        // concluding this, per STATE.md's design-gap item — not an oversight). functional.md's only
        // description of reversal is Teacher self-correction of a completion entry ("can self-correct/
        // reverse their own entry"), and the only place IsReversed is consumed anywhere in this codebase
        // is PromotionService, which excludes reversed completions from rank-progression counting — the
        // precedented meaning of "reversed" here is "doesn't count toward progression", not "never
        // happened". Nothing in the docs describes Certificate as having any lifecycle beyond
        // generation (data-design.md: "Real tracked record, not implicit"), and there is no
        // revocation/cascade rule for it anywhere. A Certificate already records a specific completion
        // event that was celebrated and (per data-design.md's open item) may already be in a parent's
        // hands — reversal correcting the underlying progression count is a separate concern from
        // retroactively unissuing a certificate. If a real data-entry mistake needs a certificate pulled
        // too, that is a distinct, undecided requirement — not something to infer from IsReversed.
        var certificates = await _db.Certificates
            .Join(_db.StudentLessonCompletions, c => c.StudentLessonCompletionId, slc => slc.Id,
                (c, slc) => new { c.GeneratedAt, c.BlobKey, slc.StudentId, slc.LessonId })
            .Where(x => studentIds.Contains(x.StudentId))
            .Join(_db.Lessons, x => x.LessonId, l => l.Id, (x, l) => new { x.StudentId, x.GeneratedAt, x.BlobKey, LessonTitle = l.Title })
            .ToListAsync(ct);

        var consents = await _db.ConsentRecords
            .Where(c => studentIds.Contains(c.StudentId))
            .Select(c => new { c.StudentId, c.Status, c.ExpiryDate })
            .ToListAsync(ct);

        var result = new List<ParentDashboardStudent>();
        foreach (var student in students.OrderBy(s => s.Name))
        {
            var studentBadges = badges
                .Where(b => b.StudentId == student.Id)
                .Select(b => new ParentDashboardBadge(b.RankName, b.AwardedAt))
                .ToList();

            var studentCertificates = certificates
                .Where(c => c.StudentId == student.Id)
                .Select(c => new ParentDashboardCertificate(c.LessonTitle, c.GeneratedAt, c.BlobKey))
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
                latestConsent?.ExpiryDate));
        }

        return result;
    }
}
