using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Attendance;

/// <summary>One student on a register: their mark (null until the register is saved with them on it) and whether consent is in force.</summary>
public sealed record RegisterRow(int StudentId, string StudentName, string RankName, int RankOrder, bool? Present, bool ConsentInForce);

/// <summary>
/// One register at one event, taken or not yet, and everyone on it: a class's (one rank), or an
/// excursion's one register for everyone (<see cref="RankId"/> null).
/// </summary>
public sealed record RegisterView(
    int EventId, DateOnly MeetingDate, string EventTitle, ChapterEventKind Kind, int? RankId, string RankName, int RankOrder,
    bool Taken, DateTime? TakenAt, string? TakenByName, IReadOnlyList<RegisterRow> Students);

/// <summary>
/// One register at an event: a class's (its counts, zero until taken, its teachers, and whether the
/// caller teaches it), or an excursion's one register for everyone (<see cref="RankId"/> null).
/// </summary>
public sealed record ClassRegisterSummary(
    int? RankId, string RankName, int RankOrder, int Students, bool Taken, int Present, int Absent,
    IReadOnlyList<string> Teachers, bool IsMine);

/// <summary>An event's class registers, the caller's own classes first, collated into the day's total.</summary>
public sealed record EventRegisters(int EventId, DateOnly Date, string Title, ChapterEventKind Kind, bool CanBeTaken, IReadOnlyList<ClassRegisterSummary> Classes)
{
    public int Present => Classes.Sum(c => c.Present);
    public int Absent => Classes.Sum(c => c.Absent);
    public int ClassesTaken => Classes.Count(c => c.Taken);
}

/// <summary>One Varangians event's collated attendance (a class day or an excursion), for the chapter's list of meeting days.</summary>
public sealed record MeetingDaySummary(int EventId, DateOnly Date, string Title, ChapterEventKind Kind, int ClassesTaken, int Present, int Absent);

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
/// The attendance registers (functional.md FR-020, FR-022; docs/design/chapters-and-attendance.md).
/// Only Varangians events have registers (<see cref="ChapterEventKinds.IsVarangians"/>). At a class,
/// every class (one per rank) has its own register, and the day's registers are collated into its
/// total; a student is on one register per day: their class's (their current rank's), or the one they
/// were already marked on. An excursion has one register for everyone (no rank). Only class marks
/// (<see cref="AttendanceMark.ClassSession"/>) count in a student's attendance history and towards
/// the at-risk rule (FR-020 as corrected 25/10/2026); an excursion's marks are its headcount.
/// Marks are present or absent only. Self-contained in the chapter: every read and write goes through
/// the chapter query filter, so an event, register, mark or student of another chapter cannot be seen
/// or marked (and <c>ChapterWriteRules</c> refuses a mark whose student is in another chapter).
///
/// <b>Roles.</b> Any Teacher in the chapter takes or updates any class's register (a class's own
/// teachers normally take it; when classes are joined one teacher takes each); Teachers and Admins
/// read registers and a student's history; Safeguarding Officers (27/10/2026) read each day's registers and totals and a student's history, but not a class's register page, and never take or correct. Checked here as well as by the page policies.
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

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// The registers of event <paramref name="eventId"/>. At a class: every class with active students,
    /// or with a register already taken, the caller's own classes first, then by rank. At an
    /// excursion: its one register for everyone. Null if the event is not this chapter's or is a
    /// parish event (no registers).
    /// </summary>
    public async Task<EventRegisters?> GetEventRegistersAsync(int eventId, CancellationToken ct = default)
    {
        RequireReader();

        var chapterEvent = await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.Date, e.Title, e.Kind })
            .SingleOrDefaultAsync(ct);
        if (chapterEvent is null || !chapterEvent.Kind.IsVarangians())
        {
            return null;
        }

        if (chapterEvent.Kind == ChapterEventKind.Excursion)
        {
            var marked = await _db.AttendanceMarks.AsNoTracking()
                .Where(m => m.AttendanceRegister!.ChapterEventId == eventId)
                .Select(m => new { m.StudentId, m.Present })
                .ToListAsync(ct);
            var markedIds = marked.Select(m => m.StudentId).ToList();
            var notYetMarked = await _db.Students.AsNoTracking()
                .CountAsync(s => s.Status == StudentStatus.Active && !markedIds.Contains(s.Id), ct);
            var taken = await _db.AttendanceRegisters.AnyAsync(r => r.ChapterEventId == eventId, ct);
            var everyone = new ClassRegisterSummary(
                null, EveryoneName, 0, marked.Count + notYetMarked, taken,
                marked.Count(m => m.Present), marked.Count(m => !m.Present), [], false);
            IReadOnlyList<ClassRegisterSummary> registersOfExcursion = everyone.Students == 0 && !taken ? [] : [everyone];
            return new EventRegisters(chapterEvent.Id, chapterEvent.Date, chapterEvent.Title, chapterEvent.Kind,
                chapterEvent.Date <= Today, registersOfExcursion);
        }

        var registers = await _db.AttendanceRegisters.AsNoTracking()
            .Where(r => r.ChapterEventId == eventId)
            .Select(r => new { r.RankId, Present = r.Marks.Count(m => m.Present), Absent = r.Marks.Count(m => !m.Present) })
            .ToDictionaryAsync(r => r.RankId!.Value, ct);
        var markedThatDay = await _db.AttendanceMarks.AsNoTracking()
            .Where(m => m.AttendanceRegister!.ChapterEventId == eventId)
            .Select(m => m.StudentId)
            .ToListAsync(ct);
        var unmarked = await _db.Students.AsNoTracking()
            .Where(s => s.Status == StudentStatus.Active && !markedThatDay.Contains(s.Id))
            .GroupBy(s => s.CurrentRankId)
            .Select(g => new { RankId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RankId, g => g.Count, ct);

        var rankIds = registers.Keys.Union(unmarked.Keys).ToList();
        var ranks = await _db.Ranks.AsNoTracking()
            .Where(r => rankIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.Order })
            .ToListAsync(ct);
        var teachers = (await _db.ClassTeachers.AsNoTracking()
                .Where(t => rankIds.Contains(t.RankId))
                .Select(t => new { t.RankId, t.TeacherUserId, t.TeacherUser!.DisplayName })
                .ToListAsync(ct))
            .GroupBy(t => t.RankId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.DisplayName).ToList());

        var classes = ranks
            .Select(rank =>
            {
                registers.TryGetValue(rank.Id, out var register);
                var rankTeachers = teachers.GetValueOrDefault(rank.Id) ?? [];
                var present = register?.Present ?? 0;
                var absent = register?.Absent ?? 0;
                return new ClassRegisterSummary(
                    rank.Id, rank.Name, rank.Order,
                    present + absent + unmarked.GetValueOrDefault(rank.Id),
                    register is not null, present, absent,
                    rankTeachers.Select(t => t.DisplayName).ToList(),
                    rankTeachers.Any(t => t.TeacherUserId == _currentUser.UserId));
            })
            .OrderByDescending(c => c.IsMine)
            .ThenBy(c => c.RankOrder)
            .ToList();

        return new EventRegisters(chapterEvent.Id, chapterEvent.Date, chapterEvent.Title, chapterEvent.Kind, chapterEvent.Date <= Today, classes);
    }

    /// <summary>
    /// A register at event <paramref name="eventId"/>. At a class, class <paramref name="rankId"/>'s:
    /// the active students at that rank not already marked on another class's register that day,
    /// plus anyone already on it. At an excursion (<paramref name="rankId"/> null), the one register
    /// for everyone: every active student, plus anyone already on it. Null if the event is not this
    /// chapter's, has no such register (a parish event, a rank at an excursion, no rank at a class),
    /// or the rank does not exist.
    /// </summary>
    public async Task<RegisterView?> GetRegisterAsync(int eventId, int? rankId, CancellationToken ct = default)
    {
        RequireTeacherOrAdmin();

        var chapterEvent = await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Date, e.Title, e.Kind })
            .SingleOrDefaultAsync(ct);
        if (chapterEvent is null || !HasRegister(chapterEvent.Kind, rankId))
        {
            return null;
        }

        var rank = rankId is null
            ? new { Name = EveryoneName, Order = 0 }
            : await _db.Ranks.AsNoTracking()
                .Where(r => r.Id == rankId)
                .Select(r => new { r.Name, r.Order })
                .SingleOrDefaultAsync(ct);
        if (rank is null)
        {
            return null;
        }

        var register = await _db.AttendanceRegisters.AsNoTracking()
            .Where(r => r.ChapterEventId == eventId && r.RankId == rankId)
            .Select(r => new { r.Id, r.TakenAt, TakenBy = r.TakenByUser != null ? r.TakenByUser.DisplayName : FormerStaff.Name })
            .SingleOrDefaultAsync(ct);
        var marks = register is null
            ? new Dictionary<int, bool>()
            : await _db.AttendanceMarks.AsNoTracking()
                .Where(m => m.AttendanceRegisterId == register.Id)
                .ToDictionaryAsync(m => m.StudentId, m => m.Present, ct);
        var elsewhere = await MarkedOnOtherRegistersAsync(eventId, rankId, ct);

        var students = await _db.Students.AsNoTracking()
            .Where(s => marks.Keys.Contains(s.Id)
                || (s.Status == StudentStatus.Active && (rankId == null || s.CurrentRankId == rankId) && !elsewhere.Contains(s.Id)))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, RankName = s.CurrentRank!.Name, RankOrder = s.CurrentRank.Order })
            .ToListAsync(ct);
        var withConsent = await ConsentInForceAsync(ct);

        return new RegisterView(
            eventId, chapterEvent.Date, chapterEvent.Title, chapterEvent.Kind, rankId, rank.Name, rank.Order,
            register is not null, register?.TakenAt, register?.TakenBy,
            students.Select(s => new RegisterRow(
                    s.Id, s.Name, s.RankName, s.RankOrder,
                    marks.TryGetValue(s.Id, out var present) ? present : null,
                    withConsent.Contains(s.Id)))
                .ToList());
    }

    /// <summary>
    /// The chapter's Varangians events (class days and excursions) from <see cref="HistoryMonths"/>
    /// months ago onwards, latest first, each with its registers collated. Parish events have no registers.
    /// </summary>
    public async Task<IReadOnlyList<MeetingDaySummary>> ListMeetingDaysAsync(CancellationToken ct = default)
    {
        RequireReader();

        var since = Today.AddMonths(-HistoryMonths);
        var days = await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Date > since && (e.Kind == ChapterEventKind.Class || e.Kind == ChapterEventKind.Excursion))
            .Select(e => new { e.Id, e.Date, e.Title, e.Kind })
            .ToListAsync(ct);
        var ids = days.Select(d => d.Id).ToList();
        var counts = (await _db.AttendanceRegisters.AsNoTracking()
                .Where(r => ids.Contains(r.ChapterEventId))
                .Select(r => new { r.ChapterEventId, Present = r.Marks.Count(m => m.Present), Absent = r.Marks.Count(m => !m.Present) })
                .ToListAsync(ct))
            .GroupBy(r => r.ChapterEventId)
            .ToDictionary(g => g.Key, g => (Classes: g.Count(), Present: g.Sum(r => r.Present), Absent: g.Sum(r => r.Absent)));

        return days
            .OrderByDescending(d => d.Date)
            .Select(d =>
            {
                var c = counts.GetValueOrDefault(d.Id);
                return new MeetingDaySummary(d.Id, d.Date, d.Title, d.Kind, c.Classes, c.Present, c.Absent);
            })
            .ToList();
    }

    /// <summary>
    /// Takes, or corrects, a register at event <paramref name="eventId"/>: class
    /// <paramref name="rankId"/>'s at a class, or the one for everyone (<paramref name="rankId"/>
    /// null) at an excursion. <paramref name="marks"/> maps student id to present (true) or absent
    /// (false); students left out keep any mark they already have. Every student must be on this
    /// register (see <see cref="GetRegisterAsync"/>). An event in the future, and a parish event, are refused.
    /// </summary>
    public async Task<RegisterSaveResult> SaveRegisterAsync(
        int eventId, int? rankId, IReadOnlyDictionary<int, bool> marks, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher role to take the register.");
        }

        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");
        var chapterEvent = await _db.ChapterEvents.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Date, e.Kind })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("That meeting day is not in this chapter's calendar.");
        if (!chapterEvent.Kind.IsVarangians())
        {
            throw new InvalidOperationException("A parish event has no register.");
        }

        if (!HasRegister(chapterEvent.Kind, rankId))
        {
            throw new InvalidOperationException(chapterEvent.Kind == ChapterEventKind.Excursion
                ? "An excursion has one register for everyone, not one per class."
                : "Choose the class whose register this is.");
        }

        if (rankId is not null && !await _db.Ranks.AnyAsync(r => r.Id == rankId, ct))
        {
            throw new InvalidOperationException("That class does not exist.");
        }

        if (chapterEvent.Date > Today)
        {
            throw new InvalidOperationException("The register can't be taken for a meeting that hasn't happened yet.");
        }

        if (marks.Count == 0)
        {
            throw new InvalidOperationException("Mark at least one student present or absent.");
        }

        var register = await _db.AttendanceRegisters
            .Include(r => r.Marks)
            .SingleOrDefaultAsync(r => r.ChapterEventId == eventId && r.RankId == rankId, ct);
        var alreadyMarked = register?.Marks.Select(m => m.StudentId).ToHashSet() ?? [];
        var elsewhere = await MarkedOnOtherRegistersAsync(eventId, rankId, ct);

        // Through the chapter filter: another chapter's student is "not found", like an unknown id.
        var studentIds = marks.Keys.ToList();
        var students = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.Status, s.CurrentRankId })
            .ToListAsync(ct);
        var onAnotherRegister = students.FirstOrDefault(s => !alreadyMarked.Contains(s.Id) && elsewhere.Contains(s.Id));
        if (onAnotherRegister is not null)
        {
            throw new InvalidOperationException($"{onAnotherRegister.Name} is already marked on another class's register for this day.");
        }

        var markable = students
            .Where(s => alreadyMarked.Contains(s.Id) || (s.Status == StudentStatus.Active && (rankId == null || s.CurrentRankId == rankId)))
            .Select(s => s.Id)
            .ToHashSet();
        if (studentIds.Any(id => !markable.Contains(id)))
        {
            throw new InvalidOperationException("The register can only mark this class's active students in this chapter.");
        }

        var wasPresent = register?.Marks.Where(m => m.Present).Select(m => m.StudentId).ToHashSet() ?? [];
        if (register is null)
        {
            register = new AttendanceRegister
            {
                Id = await _db.NextIdAsync<AttendanceRegister>(ct),
                ChapterEventId = eventId,
                RankId = rankId,
                MeetingDate = chapterEvent.Date,
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
                MeetingDate = chapterEvent.Date,
                Present = present,
                ClassSession = chapterEvent.Kind == ChapterEventKind.Class,
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
    /// Who is marked present or absent at <paramref name="meetingDate"/>, across every class's register
    /// that day (for Record completions), or an empty map if no register was taken that day.
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
        RequireReader();

        var active = await _db.Students.AnyAsync(s => s.Id == studentId && s.Status == StudentStatus.Active, ct);
        return active ? await LoadHistoryAsync(_db, studentId, DateTime.UtcNow, ct) : null;
    }

    /// <summary>
    /// A student's class marks (not excursions) over the last <see cref="HistoryMonths"/> months, newest first, with no caller
    /// check of its own: callers authorize first (the staff record above, the parent dashboard for a
    /// linked child).
    /// </summary>
    internal static async Task<AttendanceHistory> LoadHistoryAsync(VlmsDbContext db, int studentId, DateTime now, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(now).AddMonths(-HistoryMonths);
        var entries = await db.AttendanceMarks.AsNoTracking()
            .Where(m => m.StudentId == studentId && m.ClassSession && m.MeetingDate > since)
            .OrderByDescending(m => m.MeetingDate)
            .Select(m => new AttendanceEntry(m.MeetingDate, m.Present))
            .ToListAsync(ct);
        return new AttendanceHistory(entries);
    }

    /// <summary>The name shown for an excursion's one register for everyone.</summary>
    public const string EveryoneName = "Everyone";

    /// <summary>Whether an event of <paramref name="kind"/> has a register for <paramref name="rankId"/>: a class has one per rank, an excursion one with no rank.</summary>
    private static bool HasRegister(ChapterEventKind kind, int? rankId) => kind switch
    {
        ChapterEventKind.Class => rankId is not null,
        ChapterEventKind.Excursion => rankId is null,
        _ => false,
    };

    /// <summary>The students marked on another class's register at the same event.</summary>
    private async Task<HashSet<int>> MarkedOnOtherRegistersAsync(int eventId, int? rankId, CancellationToken ct) =>
        (await _db.AttendanceMarks.AsNoTracking()
            .Where(m => m.AttendanceRegister!.ChapterEventId == eventId && m.AttendanceRegister.RankId != rankId)
            .Select(m => m.StudentId)
            .ToListAsync(ct))
        .ToHashSet();

    private async Task<HashSet<int>> ConsentInForceAsync(CancellationToken ct)
    {
        var today = Today;
        return (await _db.ConsentRecords.AsNoTracking()
                .Where(c => c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
                .Select(c => c.StudentId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
    }

    /// <summary>Reading attendance (registers, totals, a student's history): Teachers, Admins and, since 27/10/2026, Safeguarding Officers. Never Approvers.</summary>
    private void RequireReader()
    {
        if (!_currentUser.HasRole(Role.Teacher) && !_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher, Admin or Safeguarding Officer role to see attendance.");
        }
    }

    private void RequireTeacherOrAdmin()
    {
        if (!_currentUser.HasRole(Role.Teacher) && !_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Teacher or Admin role to see the register.");
        }
    }
}
