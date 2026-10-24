using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Engagement;

/// <summary>
/// One row of the teacher's student list: rank and progress within it, and recent attendance
/// (FR-020): present at <see cref="RecentAttended"/> of the student's last <see cref="RecentMeetings"/>
/// registers, at most <see cref="StudentRosterService.RecentMeetingCount"/>.
/// </summary>
public sealed record StudentListItem(
    int StudentId,
    string StudentName,
    string CurrentRankName,
    int CompletedLessons,
    int TotalLessons,
    int? AssignedTeacherUserId,
    int RecentAttended = 0,
    int RecentMeetings = 0);

public sealed record StudentRecordCompletion(string LessonTitle, string RankName, DateTime CompletedAt);

public sealed record StudentRecordBadge(string RankName, DateTime AwardedAt, int RankId, bool HasImage);

public sealed record StudentRecordCertificate(int CertificateId, string RankName, DateTime GeneratedAt, string LessonTitle);

/// <summary>
/// One student's record view. Consent is the non-sensitive "current consent" status only
/// (Approved and unexpired, else none); details are on the consent page.
/// </summary>
public sealed record StudentRecord(
    int StudentId,
    string StudentName,
    string CurrentRankName,
    int CompletedLessons,
    int TotalLessons,
    IReadOnlyList<StudentRecordCompletion> Completions,
    IReadOnlyList<StudentRecordBadge> Badges,
    IReadOnlyList<StudentRecordCertificate> Certificates,
    bool HasCurrentConsent,
    DateOnly? ConsentExpiryDate,
    ConsentStatus? LatestSubmissionStatus);

/// <summary>
/// Teacher's student list and record view (functional.md FR-009). Teacher and Admin only (role
/// checked here as defence in depth on top of the page policy). Any such caller sees all
/// <see cref="StudentStatus.Active"/> students; <c>assignedToMeOnly</c> narrows to
/// <see cref="Student.AssignedTeacherUserId"/> == caller. Progress is the count of the rank's active
/// lessons that have a non-reversed completion, out of the rank's active lessons. A reversed
/// completion is not complete, and its certificate is not listed (as on the parent dashboard). No
/// sensitive consent data is read.
/// </summary>
public sealed class StudentRosterService
{
    /// <summary>How many of a student's latest registers the list's "recent attendance" covers.</summary>
    public const int RecentMeetingCount = 3;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public StudentRosterService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<StudentListItem>> ListActiveStudentsAsync(bool assignedToMeOnly = false, CancellationToken ct = default)
    {
        RequireStaff();

        var query = _db.Students.Where(s => s.Status == StudentStatus.Active);
        if (assignedToMeOnly)
        {
            var userId = _currentUser.UserId;
            if (userId is null)
            {
                return [];
            }

            query = query.Where(s => s.AssignedTeacherUserId == userId);
        }

        var students = await query
            .Join(_db.Ranks, s => s.CurrentRankId, r => r.Id,
                (s, r) => new { s.Id, s.Name, s.CurrentRankId, s.AssignedTeacherUserId, RankName = r.Name })
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        if (students.Count == 0)
        {
            return [];
        }

        var studentIds = students.Select(s => s.Id).ToList();
        var rankIds = students.Select(s => s.CurrentRankId).Distinct().ToList();

        var lessonsByRank = (await _db.Lessons
                .Where(l => l.IsActive && rankIds.Contains(l.RankId))
                .Select(l => new { l.Id, l.RankId })
                .ToListAsync(ct))
            .GroupBy(l => l.RankId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Id).ToHashSet());

        var completions = (await _db.StudentLessonCompletions
                .Where(c => studentIds.Contains(c.StudentId) && !c.IsReversed)
                .Select(c => new { c.StudentId, c.LessonId })
                .ToListAsync(ct))
            .GroupBy(c => c.StudentId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.LessonId).ToHashSet());

        var recentMarks = (await _db.AttendanceMarks
                .Where(m => studentIds.Contains(m.StudentId))
                .Select(m => new { m.StudentId, m.MeetingDate, m.Present })
                .ToListAsync(ct))
            .GroupBy(m => m.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MeetingDate).Take(RecentMeetingCount).ToList());

        return students.Select(s =>
        {
            var rankLessons = lessonsByRank.GetValueOrDefault(s.CurrentRankId) ?? [];
            var done = completions.GetValueOrDefault(s.Id) ?? [];
            var marks = recentMarks.GetValueOrDefault(s.Id) ?? [];
            return new StudentListItem(
                s.Id, s.Name, s.RankName, rankLessons.Count(done.Contains), rankLessons.Count, s.AssignedTeacherUserId,
                marks.Count(m => m.Present), marks.Count);
        }).ToList();
    }

    /// <summary>One active student's record, or null if unknown or not active.</summary>
    public async Task<StudentRecord?> GetRecordAsync(int studentId, CancellationToken ct = default)
    {
        RequireStaff();
        return await LoadRecordAsync(_db, studentId, activeOnly: true, ct);
    }

    /// <summary>
    /// One active student's journey through every rank (the student page's "Through the ranks", shown
    /// to staff on the student record), or null if unknown or not active.
    /// </summary>
    public async Task<StudentJourney?> GetJourneyAsync(int studentId, CancellationToken ct = default)
    {
        RequireStaff();
        var active = await _db.Students.AnyAsync(s => s.Id == studentId && s.Status == StudentStatus.Active, ct);
        return active ? await StudentJourney.LoadAsync(_db, studentId, ct) : null;
    }

    /// <summary>
    /// Builds one student's record with no caller check of its own. Shared with
    /// <see cref="StudentPageService"/> (FR-011), which resolves the caller's own student id first;
    /// every caller must do its own authorization before calling this.
    /// </summary>
    internal static async Task<StudentRecord?> LoadRecordAsync(VlmsDbContext db, int studentId, bool activeOnly, CancellationToken ct)
    {
        var student = await db.Students
            .Where(s => s.Id == studentId && (!activeOnly || s.Status == StudentStatus.Active))
            .Join(db.Ranks, s => s.CurrentRankId, r => r.Id,
                (s, r) => new { s.Id, s.Name, s.CurrentRankId, RankName = r.Name })
            .SingleOrDefaultAsync(ct);
        if (student is null)
        {
            return null;
        }

        var completionRows = await db.StudentLessonCompletions
            .Where(c => c.StudentId == studentId && !c.IsReversed)
            .Join(db.Lessons, c => c.LessonId, l => l.Id,
                (c, l) => new { c.Id, c.LessonId, l.RankId, LessonTitle = l.Title, c.CompletedAt })
            .Join(db.Ranks, x => x.RankId, r => r.Id,
                (x, r) => new { x.Id, x.LessonId, x.LessonTitle, x.CompletedAt, RankName = r.Name })
            .ToListAsync(ct);

        var rankLessonIds = await db.Lessons
            .Where(l => l.IsActive && l.RankId == student.CurrentRankId)
            .Select(l => l.Id)
            .ToListAsync(ct);
        var completed = completionRows.Where(c => rankLessonIds.Contains(c.LessonId)).Select(c => c.LessonId).Distinct().Count();

        var badges = (await db.StudentBadges
                .Where(b => b.StudentId == studentId)
                .Join(db.RankBadges, b => b.RankBadgeId, rb => rb.Id, (b, rb) => new { b.AwardedAt, rb.RankId, rb.ImageBlobKey })
                .Join(db.Ranks, x => x.RankId, r => r.Id, (x, r) => new { x.AwardedAt, x.RankId, HasImage = x.ImageBlobKey != null, RankName = r.Name })
                .ToListAsync(ct))
            .OrderBy(b => b.AwardedAt)
            .Select(b => new StudentRecordBadge(b.RankName, b.AwardedAt, b.RankId, b.HasImage))
            .ToList();

        var certificateRows = await db.Certificates
            .Where(c => db.StudentLessonCompletions.Any(slc =>
                slc.Id == c.StudentLessonCompletionId && slc.StudentId == studentId && !slc.IsReversed))
            .Select(c => new { c.Id, c.GeneratedAt, c.StudentLessonCompletionId })
            .ToListAsync(ct);
        var completionById = completionRows.ToDictionary(c => c.Id);
        var certificates = certificateRows
            .OrderBy(c => c.GeneratedAt)
            .Select(c => new StudentRecordCertificate(
                c.Id,
                completionById.GetValueOrDefault(c.StudentLessonCompletionId)?.RankName ?? "",
                c.GeneratedAt,
                completionById.GetValueOrDefault(c.StudentLessonCompletionId)?.LessonTitle ?? ""))
            .ToList();

        var consents = await db.ConsentRecords
            .Where(c => c.StudentId == studentId)
            .Select(c => new { c.Id, c.Status, c.ExpiryDate })
            .ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = consents
            .Where(c => c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
            .OrderByDescending(c => c.ExpiryDate)
            .FirstOrDefault();

        return new StudentRecord(
            student.Id,
            student.Name,
            student.RankName,
            completed,
            rankLessonIds.Count,
            completionRows.OrderBy(c => c.CompletedAt).Select(c => new StudentRecordCompletion(c.LessonTitle, c.RankName, c.CompletedAt)).ToList(),
            badges,
            certificates,
            current is not null,
            current?.ExpiryDate,
            consents.OrderByDescending(c => c.Id).FirstOrDefault()?.Status);
    }

    private void RequireStaff()
    {
        if (!_currentUser.HasRole(Role.Teacher) && !_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher or Admin role to view students.");
        }
    }
}
