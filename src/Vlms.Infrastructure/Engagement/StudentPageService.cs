using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Engagement;

/// <summary>
/// The signed-in student's own page (functional.md FR-011, journey J4 steps 3–4): rank and progress
/// in it, completed lessons, badges and certificates. Takes no student id: it resolves the caller's
/// own <see cref="Student"/> through <see cref="Student.AppUserId"/>, so a student can only ever see
/// their own record. <see cref="Role.Student"/> only, checked here as well as by the page policy.
///
/// Reuses <see cref="StudentRosterService.LoadRecordAsync"/> (the teacher's record view) rather than
/// a second copy of the same joins, so progress, reversed completions and hidden certificates are
/// counted the same way for teacher, parent and student. Unlike the teacher view it does not require
/// the student to be <see cref="StudentStatus.Active"/>: a graduated student keeps their page and
/// certificates. The record's non-sensitive consent fields are not shown on the student page.
/// </summary>
public sealed class StudentPageService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public StudentPageService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>The caller's own record, or null if no student record is linked to their sign-in.</summary>
    public async Task<StudentRecord?> GetOwnRecordAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Student))
        {
            throw new UnauthorizedAccessException("Caller must hold the Student role to view the student page.");
        }

        if (_currentUser.UserId is not int userId)
        {
            return null;
        }

        var studentId = await _db.Students
            .Where(s => s.AppUserId == userId)
            .Select(s => (int?)s.Id)
            .SingleOrDefaultAsync(ct);

        return studentId is int id
            ? await StudentRosterService.LoadRecordAsync(_db, id, activeOnly: false, ct)
            : null;
    }

    /// <summary>
    /// The caller's own journey through every rank (the student page's rank journey), or null if no
    /// student record is linked to their sign-in. Same caller checks as <see cref="GetOwnRecordAsync"/>.
    /// </summary>
    public async Task<StudentJourney?> GetOwnJourneyAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Student))
        {
            throw new UnauthorizedAccessException("Caller must hold the Student role to view the student page.");
        }

        if (_currentUser.UserId is not int userId)
        {
            return null;
        }

        var studentId = await _db.Students
            .Where(s => s.AppUserId == userId)
            .Select(s => (int?)s.Id)
            .SingleOrDefaultAsync(ct);

        return studentId is int id ? await StudentJourney.LoadAsync(_db, id, ct) : null;
    }
}
