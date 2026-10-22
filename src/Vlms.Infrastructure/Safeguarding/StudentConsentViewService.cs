using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>One active student's consent status, for the teacher's list (no sensitive fields).</summary>
public sealed record StudentConsentStatus(
    int StudentId,
    string StudentName,
    ConsentSubmissionSummary? CurrentConsent,
    ConsentStatus? LatestSubmissionStatus);

/// <summary>
/// One student's consent for the teacher's read-only page. <see cref="CurrentDetails"/> are the
/// sensitive details of <see cref="CurrentConsent"/> — the latest Approved, unexpired submission —
/// or null when there is none.
/// </summary>
public sealed record StudentConsentDetail(
    int StudentId,
    string StudentName,
    DateOnly DateOfBirth,
    ConsentSubmissionSummary? CurrentConsent,
    ConsentDetailsView? CurrentDetails,
    ConsentStatus? LatestSubmissionStatus);

/// <summary>
/// Read-only consent view for Teachers (functional.md FR-015, adr/0006-parent-consent-and-teacher-read-access.md):
/// teachers can see each student's consent status and the medical, dietary/SEN and emergency
/// contact details from the student's current approved consent. Admin and Safeguarding Officer may
/// use it too. The Approver role, Parents and Students are refused here — a Parent has
/// <see cref="ParentConsentService"/> for their own children.
///
/// There are no write methods: Teachers have read access only. The service role check is defence in
/// depth on top of the page's <c>RequireTeacher</c> policy and the <see cref="ConsentSensitiveDetails"/>
/// query filter; the details are materialized as whole entities so the
/// <see cref="Auditing.SensitiveDataAuditInterceptor"/> logs the read.
///
/// Only the current Approved submission's details are shown, not a pending one's: what a teacher
/// acts on should be what a Safeguarding Officer or Admin has reviewed.
/// </summary>
public sealed class StudentConsentViewService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public StudentConsentViewService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Every active student with their current consent status. No sensitive data is read.</summary>
    public async Task<IReadOnlyList<StudentConsentStatus>> ListActiveStudentsAsync(CancellationToken ct = default)
    {
        RequireStaff();

        var students = await _db.Students
            .Where(s => s.Status == StudentStatus.Active)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var studentIds = students.Select(s => s.Id).ToList();
        var records = await _db.ConsentRecords.Where(c => studentIds.Contains(c.StudentId)).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return students.Select(s =>
        {
            var forStudent = records.Where(c => c.StudentId == s.Id).ToList();
            var current = CurrentOf(forStudent, today);
            return new StudentConsentStatus(
                s.Id,
                s.Name,
                current is null ? null : ConsentSubmissionSummary.From(current),
                forStudent.OrderByDescending(c => c.Id).FirstOrDefault()?.Status);
        }).ToList();
    }

    /// <summary>
    /// One student's consent, including the current approved submission's sensitive details (one
    /// audited read). Returns null for an unknown student.
    /// </summary>
    public async Task<StudentConsentDetail?> GetStudentAsync(int studentId, CancellationToken ct = default)
    {
        RequireStaff();

        var student = await _db.Students.SingleOrDefaultAsync(s => s.Id == studentId, ct);
        if (student is null)
        {
            return null;
        }

        var records = await _db.ConsentRecords.Where(c => c.StudentId == studentId).ToListAsync(ct);
        var current = CurrentOf(records, DateOnly.FromDateTime(DateTime.UtcNow));

        ConsentSensitiveDetails? details = null;
        if (current is not null)
        {
            details = await _db.ConsentSensitiveDetails.SingleOrDefaultAsync(d => d.ConsentRecordId == current.Id, ct);
        }

        return new StudentConsentDetail(
            student.Id,
            student.Name,
            student.DateOfBirth,
            current is null ? null : ConsentSubmissionSummary.From(current),
            details is null ? null : new ConsentDetailsView(details.EmergencyContact, details.EmergencyMedicalInfo, details.DietarySEN),
            records.OrderByDescending(c => c.Id).FirstOrDefault()?.Status);
    }

    // Same "approved and unexpired" test CompletionService applies for the FR-003 block.
    private static ConsentRecord? CurrentOf(IEnumerable<ConsentRecord> records, DateOnly today) =>
        records
            .Where(c => c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
            .OrderByDescending(c => c.ExpiryDate)
            .ThenByDescending(c => c.Id)
            .FirstOrDefault();

    private void RequireStaff()
    {
        if (!_currentUser.HasRole(Role.Teacher)
            && !_currentUser.HasRole(Role.Admin)
            && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Teacher, Admin or SafeguardingOfficer role to view student consent.");
        }
    }
}
