using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Infrastructure.Attendance;

/// <summary>A teacher of a class.</summary>
public sealed record ClassTeacherItem(int UserId, string DisplayName);

/// <summary>One class (rank) and its teachers.</summary>
public sealed record ClassWithTeachers(int RankId, string RankName, int RankOrder, IReadOnlyList<ClassTeacherItem> Teachers);

/// <summary>
/// Which Teachers teach which class (functional.md FR-022): classes are divided by rank, one class per
/// rank; an Admin assigns Teachers to classes on the chapter's staff page. A Teacher can teach several
/// classes and a class can have several Teachers. Only people who hold Teacher in the chapter and have
/// not left can be assigned. The assignment puts a teacher's own classes first on a meeting day's
/// registers; it does not limit which registers they can take.
/// </summary>
public sealed class ClassTeacherService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ClassTeacherService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Every class, lowest rank first, with its teachers in this chapter.</summary>
    public async Task<IReadOnlyList<ClassWithTeachers>> ListAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var assignments = (await _db.ClassTeachers.AsNoTracking()
                .Select(t => new { t.RankId, t.TeacherUserId, t.TeacherUser!.DisplayName })
                .ToListAsync(ct))
            .ToLookup(t => t.RankId);
        var ranks = await _db.Ranks.AsNoTracking().OrderBy(r => r.Order).Select(r => new { r.Id, r.Name, r.Order }).ToListAsync(ct);

        return ranks
            .Select(r => new ClassWithTeachers(r.Id, r.Name, r.Order,
                assignments[r.Id].OrderBy(t => t.DisplayName).Select(t => new ClassTeacherItem(t.TeacherUserId, t.DisplayName)).ToList()))
            .ToList();
    }

    /// <summary>The people who can be assigned: Teachers in this chapter who have not left.</summary>
    public async Task<IReadOnlyList<ClassTeacherItem>> ListTeachersAsync(CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        return await ChapterMembership.StaffIn(_db, chapterId, Role.Teacher)
            .OrderBy(u => u.DisplayName)
            .Select(u => new ClassTeacherItem(u.Id, u.DisplayName))
            .ToListAsync(ct);
    }

    /// <summary>Assigns a Teacher to a class. Assigning someone already assigned does nothing.</summary>
    public async Task AssignAsync(int rankId, int teacherUserId, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();
        if (!await _db.Ranks.AnyAsync(r => r.Id == rankId, ct))
        {
            throw new InvalidOperationException("That class does not exist.");
        }

        if (!await ChapterMembership.StaffIn(_db, chapterId, Role.Teacher).AnyAsync(u => u.Id == teacherUserId, ct))
        {
            throw new InvalidOperationException("Only a Teacher in this chapter can be assigned to a class.");
        }

        if (await _db.ClassTeachers.AnyAsync(t => t.RankId == rankId && t.TeacherUserId == teacherUserId, ct))
        {
            return;
        }

        _db.ClassTeachers.Add(new ClassTeacher
        {
            Id = await _db.NextIdAsync<ClassTeacher>(ct),
            RankId = rankId,
            TeacherUserId = teacherUserId,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a Teacher from a class.</summary>
    public async Task RemoveAsync(int rankId, int teacherUserId, CancellationToken ct = default)
    {
        RequireAdmin();
        var assignment = await _db.ClassTeachers.SingleOrDefaultAsync(t => t.RankId == rankId && t.TeacherUserId == teacherUserId, ct);
        if (assignment is null)
        {
            return;
        }

        _db.ClassTeachers.Remove(assignment);
        await _db.SaveChangesAsync(ct);
    }

    private int RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must be an Admin of the chapter to assign class teachers.");
        }

        return chapterId;
    }
}
