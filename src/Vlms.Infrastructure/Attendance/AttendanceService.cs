using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Attendance;

/// <summary>One student on a register: their mark (null until the register is saved with them on it) and whether consent is in force.</summary>
public sealed record RegisterRow(int StudentId, string StudentName, string RankName, int RankOrder, bool? Present, bool ConsentInForce);

/// <summary>The register for one meeting date: taken or not yet, and every student on roll.</summary>
public sealed record RegisterView(DateOnly MeetingDate, bool Taken, DateTime? TakenAt, string? TakenByName, IReadOnlyList<RegisterRow> Students);

/// <summary>One register already taken, for the chapter's list of meetings.</summary>
public sealed record RegisterSummary(DateOnly MeetingDate, int Present, int Absent);

/// <summary>What a save did: the marks now on the register, and who attended without consent in force for the first time on it.</summary>
public sealed record RegisterSaveResult(int Present, int Absent, IReadOnlyList<int> PresentWithoutConsent);

/// <summary>One mark in a student's attendance history.</summary>
public sealed record AttendanceEntry(DateOnly MeetingDate, bool Present);

/// <summary>A student's attendance over a period, newest first.</summary>
public sealed record AttendanceHistory(IReadOnlyList<AttendanceEntry> Entries)
{
    public int Sessions => Entries.Count;
    public int Attended => Entries.Count(e => e.Present);
}

/// <summary>
/// The attendance register (functional.md FR-020; docs/design/chapters-and-attendance.md sections 3 and 5).
/// One register per chapter per meeting date; marks are present or absent only. Self-contained in
/// the chapter: every read and write goes through the chapter query filter, so a register, a mark or
/// a student of another chapter cannot be seen or marked (and <c>ChapterWriteRules</c> refuses a mark
/// whose student is in another chapter).
///
/// <b>Roles.</b> Teachers take the register (as they record completions); Teachers and Admins read
/// it and a student's history. Checked here as well as by the page policies.
///
/// <b>Consent.</b> A child without consent in force can still be marked present (they were there);
/// the register shows the warning, and the save reports who attended without consent so the caller
/// can tell the chapter's Safeguarding Officers (<see cref="AttendanceNotificationService"/>).
/// Completions stay blocked by <c>CompletionService</c>, unchanged.
/// </summary>
public sealed class AttendanceService
{
    /// <summary>The period shown on the student record and the parent dashboard (design section 3: "the last 12 months").</summary>
    public const int HistoryMonths = 12;

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public AttendanceService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>The register for <paramref name="meetingDate"/>: every active student, with their mark if it has been taken.</summary>
    public async Task<RegisterView> GetRegisterAsync(DateOnly meetingDate, CancellationToken ct = default)
    {
        RequireTeacherOrAdmin();

        var register = await _db.AttendanceRegisters.AsNoTracking()
            .Where(r => r.MeetingDate == meetingDate)
            .Select(r => new { r.Id, r.TakenAt, TakenBy = r.TakenByUser!.DisplayName })
            .SingleOrDefaultAsync(ct);
        var marks = register is null
            ? new Dictionary<int, bool>()
            : await _db.AttendanceMarks.AsNoTracking()
                .Where(m => m.AttendanceRegisterId == register.Id)
                .ToDictionaryAsync(m => m.StudentId, m => m.Present, ct);

        // Active students, plus anyone already marked on this register who has since left.
        var students = await _db.Students.AsNoTracking()
            .Where(s => s.Status == StudentStatus.Active || marks.Keys.Contains(s.Id))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, RankName = s.CurrentRank!.Name, RankOrder = s.CurrentRank.Order })
            .ToListAsync(ct);
        var withConsent = await ConsentInForceAsync(ct);

        return new RegisterView(
            meetingDate,
            register is not null,
            register?.TakenAt,
            register?.TakenBy,
            students.Select(s => new RegisterRow(
                    s.Id, s.Name, s.RankName, s.RankOrder,
                    marks.TryGetValue(s.Id, out var present) ? present : null,
                    withConsent.Contains(s.Id)))
                .ToList());
    }

    /// <summary>The chapter's registers, newest first.</summary>
    public async Task<IReadOnlyList<RegisterSummary>> ListRegistersAsync(int take = 24, CancellationToken ct = default)
    {
        RequireTeacherOrAdmin();

        return await _db.AttendanceRegisters.AsNoTracking()
            .OrderByDescending(r => r.MeetingDate)
            .Take(take)
            .Select(r => new RegisterSummary(r.MeetingDate, r.Marks.Count(m => m.Present), r.Marks.Count(m => !m.Present)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Takes, or corrects, the register for <paramref name="meetingDate"/> in the caller's chapter.
    /// <paramref name="marks"/> maps student id to present (true) or absent (false); students left out
    /// keep any mark they already have. Every student must be an active student the caller can see.
    /// A meeting date in the future is refused.
    /// </summary>
    public async Task<RegisterSaveResult> SaveRegisterAsync(
        DateOnly meetingDate, IReadOnlyDictionary<int, bool> marks, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher role to take the register.");
        }

        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
        if (meetingDate > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new InvalidOperationException("The register can't be taken for a meeting that hasn't happened yet.");
        }

        if (marks.Count == 0)
        {
            throw new InvalidOperationException("Mark at least one student present or absent.");
        }

        var register = await _db.AttendanceRegisters
            .Include(r => r.Marks)
            .SingleOrDefaultAsync(r => r.MeetingDate == meetingDate, ct);
        var alreadyMarked = register?.Marks.Select(m => m.StudentId).ToHashSet() ?? [];

        // Through the chapter filter: another chapter's student is "not found", like an unknown id.
        var studentIds = marks.Keys.ToList();
        var markable = await _db.Students
            .Where(s => studentIds.Contains(s.Id) && (s.Status == StudentStatus.Active || alreadyMarked.Contains(s.Id)))
            .Select(s => s.Id)
            .ToListAsync(ct);
        var unknown = studentIds.Except(markable).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException("The register can only mark active students in this chapter.");
        }

        var wasPresent = register?.Marks.Where(m => m.Present).Select(m => m.StudentId).ToHashSet() ?? [];
        if (register is null)
        {
            register = new AttendanceRegister
            {
                Id = await _db.NextIdAsync<AttendanceRegister>(ct),
                MeetingDate = meetingDate,
                TakenByUserId = userId,
                TakenAt = DateTime.UtcNow,
            };
            _db.AttendanceRegisters.Add(register);
        }
        else
        {
            register.TakenByUserId = userId;
            register.TakenAt = DateTime.UtcNow;
        }

        var nextMarkId = await _db.NextIdAsync<AttendanceMark>(ct);
        foreach (var (studentId, present) in marks)
        {
            var existing = register.Marks.FirstOrDefault(m => m.StudentId == studentId);
            if (existing is not null)
            {
                existing.Present = present;
                continue;
            }

            var mark = new AttendanceMark
            {
                Id = nextMarkId++,
                AttendanceRegisterId = register.Id,
                StudentId = studentId,
                MeetingDate = meetingDate,
                Present = present,
            };
            register.Marks.Add(mark);
        }

        await _db.SaveChangesAsync(ct);

        var withConsent = await ConsentInForceAsync(ct);
        var newlyPresentWithoutConsent = marks
            .Where(m => m.Value && !wasPresent.Contains(m.Key) && !withConsent.Contains(m.Key))
            .Select(m => m.Key)
            .Order()
            .ToList();

        return new RegisterSaveResult(
            register.Marks.Count(m => m.Present),
            register.Marks.Count(m => !m.Present),
            newlyPresentWithoutConsent);
    }

    /// <summary>
    /// Who is marked present or absent at <paramref name="meetingDate"/> (for Record completions), or
    /// an empty map if no register was taken that day.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, bool>> GetMarksAsync(DateOnly meetingDate, CancellationToken ct = default)
    {
        RequireTeacherOrAdmin();

        return await _db.AttendanceMarks.AsNoTracking()
            .Where(m => m.MeetingDate == meetingDate)
            .ToDictionaryAsync(m => m.StudentId, m => m.Present, ct);
    }

    /// <summary>One active student's attendance over the last <see cref="HistoryMonths"/> months (the student record).</summary>
    public async Task<AttendanceHistory?> GetStudentHistoryAsync(int studentId, CancellationToken ct = default)
    {
        RequireTeacherOrAdmin();

        var active = await _db.Students.AnyAsync(s => s.Id == studentId && s.Status == StudentStatus.Active, ct);
        return active ? await LoadHistoryAsync(_db, studentId, DateTime.UtcNow, ct) : null;
    }

    /// <summary>
    /// A student's marks over the last <see cref="HistoryMonths"/> months, newest first, with no caller
    /// check of its own: callers authorize first (the staff record above, the parent dashboard for a
    /// linked child).
    /// </summary>
    internal static async Task<AttendanceHistory> LoadHistoryAsync(VlmsDbContext db, int studentId, DateTime now, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(now).AddMonths(-HistoryMonths);
        var entries = await db.AttendanceMarks.AsNoTracking()
            .Where(m => m.StudentId == studentId && m.MeetingDate > since)
            .OrderByDescending(m => m.MeetingDate)
            .Select(m => new AttendanceEntry(m.MeetingDate, m.Present))
            .ToListAsync(ct);
        return new AttendanceHistory(entries);
    }

    private async Task<HashSet<int>> ConsentInForceAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (await _db.ConsentRecords.AsNoTracking()
                .Where(c => c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
                .Select(c => c.StudentId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
    }

    private void RequireTeacherOrAdmin()
    {
        if (!_currentUser.HasRole(Role.Teacher) && !_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher or Admin role to see the register.");
        }
    }
}
