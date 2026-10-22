using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>One consent submission's non-sensitive fields, for display.</summary>
public sealed record ConsentSubmissionSummary(
    int ConsentRecordId,
    ConsentStatus Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly ExpiryDate,
    DateTime? SubmittedAt,
    DateTime? DecidedAt,
    string? DecisionComments,
    bool PhotoMediaConsent,
    bool TransportOffsiteConsent,
    bool DataSharingConsent)
{
    public static ConsentSubmissionSummary From(ConsentRecord record) => new(
        record.Id, record.Status, record.PeriodStart, record.PeriodEnd, record.ExpiryDate,
        record.SubmittedAt, record.DecidedAt, record.DecisionComments,
        record.PhotoMediaConsent, record.TransportOffsiteConsent, record.DataSharingConsent);
}

/// <summary>The sensitive fields of one submission (<see cref="ConsentSensitiveDetails"/>).</summary>
public sealed record ConsentDetailsView(string EmergencyContact, string? EmergencyMedicalInfo, string? DietarySEN);

/// <summary>
/// One linked child on the parent's consent page. <see cref="CurrentConsent"/> is the latest
/// Approved, unexpired submission (what FR-003's completion block looks for), or null.
/// <see cref="LatestSubmission"/> is the newest submission of any status, and
/// <see cref="LatestDetails"/> its sensitive details, used to pre-fill the form.
/// </summary>
public sealed record ParentConsentChild(
    int StudentId,
    string StudentName,
    DateOnly DateOfBirth,
    ConsentSubmissionSummary? CurrentConsent,
    ConsentSubmissionSummary? LatestSubmission,
    ConsentDetailsView? LatestDetails);

/// <summary>What a parent enters on the annual consent form (functional.md FR-001, FR-013, FR-015).</summary>
public sealed record ParentConsentForm(
    string StudentName,
    DateOnly StudentDateOfBirth,
    bool PhotoMediaConsent,
    bool TransportOffsiteConsent,
    bool DataSharingConsent,
    string EmergencyContact,
    string? EmergencyMedicalInfo,
    string? DietarySEN);

/// <summary>
/// The parent's own annual consent form (functional.md FR-015, user-journeys.md J5 step 6,
/// adr/0006-parent-consent-and-teacher-read-access.md).
///
/// <b>Authorization, in the service (defence in depth, same pattern as every other service):</b>
/// the caller must hold <see cref="Role.Parent"/>, have a resolved UserId, not have left, and be the
/// <see cref="ParentGuardian.AppUserId"/> on a <see cref="StudentGuardianLink"/> to the student.
/// The <see cref="ConsentSensitiveDetails"/> query filter already confines a Parent's reads to their
/// own linked children, but a query filter does not govern writes at all — so
/// <see cref="SubmitAsync"/> checks the link itself before adding anything.
///
/// <b>History, not overwrite:</b> every submission is a new Pending <see cref="ConsentRecord"/> plus
/// its own <see cref="ConsentSensitiveDetails"/> row. An earlier Approved record stays Approved
/// (and keeps satisfying FR-003) until a Safeguarding Officer or Admin decides the new one via
/// <see cref="ConsentRecordService.DecideAsync"/>. A second submission while one is still Pending
/// is refused, so the reviewer never has two competing versions to choose between.
///
/// <b>Period:</b> a submission covers one year from the day it is submitted
/// (<see cref="ConsentRecord.PeriodStart"/> = today, <see cref="ConsentRecord.PeriodEnd"/> =
/// <see cref="ConsentRecord.ExpiryDate"/> = a year later less a day). Approval does not move it.
///
/// Reads materialize whole <see cref="ConsentSensitiveDetails"/> entities (never a column
/// projection), so <see cref="Auditing.SensitiveDataAuditInterceptor"/> logs each one. Only the
/// latest submission's details are read per child, to keep the parent's reads to what the page
/// needs.
/// </summary>
public sealed class ParentConsentService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ParentConsentService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Lists the caller's own linked children with their consent status. Parent only.</summary>
    public async Task<IReadOnlyList<ParentConsentChild>> GetMyChildrenAsync(CancellationToken ct = default)
    {
        var userId = await RequireActiveParentAsync(ct);

        var studentIds = await LinkedStudentIds(userId).Distinct().ToListAsync(ct);
        if (studentIds.Count == 0)
        {
            return [];
        }

        var students = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        var records = await _db.ConsentRecords
            .Where(c => studentIds.Contains(c.StudentId))
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var latestByStudent = records
            .GroupBy(c => c.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Id).First());

        var latestIds = latestByStudent.Values.Select(c => c.Id).ToList();
        var details = await _db.ConsentSensitiveDetails
            .Where(d => latestIds.Contains(d.ConsentRecordId))
            .ToListAsync(ct);
        var detailsByRecordId = details.ToDictionary(d => d.ConsentRecordId);

        return students.Select(student =>
        {
            var current = records
                .Where(c => c.StudentId == student.Id && c.Status == ConsentStatus.Approved && c.ExpiryDate >= today)
                .OrderByDescending(c => c.ExpiryDate)
                .ThenByDescending(c => c.Id)
                .FirstOrDefault();
            var latest = latestByStudent.GetValueOrDefault(student.Id);
            var latestDetails = latest is null ? null : detailsByRecordId.GetValueOrDefault(latest.Id);

            return new ParentConsentChild(
                student.Id,
                student.Name,
                student.DateOfBirth,
                current is null ? null : ConsentSubmissionSummary.From(current),
                latest is null ? null : ConsentSubmissionSummary.From(latest),
                latestDetails is null
                    ? null
                    : new ConsentDetailsView(latestDetails.EmergencyContact, latestDetails.EmergencyMedicalInfo, latestDetails.DietarySEN));
        }).ToList();
    }

    /// <summary>
    /// Submits the annual consent form for one of the caller's own linked children, creating a
    /// Pending <see cref="ConsentRecord"/> and its <see cref="ConsentSensitiveDetails"/> in one
    /// <c>SaveChangesAsync</c>. Parent only, own linked children only.
    /// </summary>
    public async Task<ConsentRecord> SubmitAsync(int studentId, ParentConsentForm form, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        var userId = await RequireActiveParentAsync(ct);

        var parentGuardianId = await _db.StudentGuardianLinks
            .Where(l => l.StudentId == studentId && l.ParentGuardian!.AppUserId == userId)
            .Select(l => (int?)l.ParentGuardianId)
            .FirstOrDefaultAsync(ct);
        if (parentGuardianId is null)
        {
            throw new UnauthorizedAccessException("You can only submit a consent form for a child linked to your account.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(form.StudentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(form.EmergencyContact);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (form.StudentDateOfBirth > today)
        {
            throw new ArgumentException("Date of birth cannot be in the future.", nameof(form));
        }

        if (await _db.ConsentRecords.AnyAsync(c => c.StudentId == studentId && c.Status == ConsentStatus.Pending, ct))
        {
            throw new InvalidOperationException(
                "A consent form for this child is already awaiting review. You can submit changes once it has been approved or rejected.");
        }

        var periodEnd = today.AddYears(1).AddDays(-1);
        var recordId = (await _db.ConsentRecords.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
        var record = new ConsentRecord
        {
            Id = recordId,
            StudentId = studentId,
            PeriodStart = today,
            PeriodEnd = periodEnd,
            PhotoMediaConsent = form.PhotoMediaConsent,
            TransportOffsiteConsent = form.TransportOffsiteConsent,
            DataSharingConsent = form.DataSharingConsent,
            Status = ConsentStatus.Pending,
            SubmittedByParentId = parentGuardianId.Value,
            ExpiryDate = periodEnd,
            SubmittedAt = DateTime.UtcNow,
            SubmittedByUserId = userId,
            StudentName = form.StudentName.Trim(),
            StudentDateOfBirth = form.StudentDateOfBirth
        };
        _db.ConsentRecords.Add(record);

        // Id = the record's own id (ConsentRecordService.DetailsIdFor), never MAX(id)+1 over
        // ConsentSensitiveDetails: that MAX runs through the caller's query filter, and a Parent
        // sees only their own children's rows, so it would hand out ids already in use.
        _db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = ConsentRecordService.DetailsIdFor(recordId),
            ConsentRecordId = recordId,
            EmergencyContact = form.EmergencyContact.Trim(),
            EmergencyMedicalInfo = string.IsNullOrWhiteSpace(form.EmergencyMedicalInfo) ? null : form.EmergencyMedicalInfo.Trim(),
            DietarySEN = string.IsNullOrWhiteSpace(form.DietarySEN) ? null : form.DietarySEN.Trim()
        });

        await _db.SaveChangesAsync(ct);
        return record;
    }

    private IQueryable<int> LinkedStudentIds(int userId) =>
        _db.StudentGuardianLinks
            .Where(l => l.ParentGuardian!.AppUserId == userId)
            .Select(l => l.StudentId);

    private async Task<int> RequireActiveParentAsync(CancellationToken ct)
    {
        if (!_currentUser.HasRole(Role.Parent) || _currentUser.UserId is not int userId)
        {
            throw new UnauthorizedAccessException("Caller must hold the Parent role to use the consent form.");
        }

        var hasLeft = await _db.AppUsers.Where(u => u.Id == userId).Select(u => (bool?)u.HasLeft).SingleOrDefaultAsync(ct);
        if (hasLeft is not false)
        {
            throw new UnauthorizedAccessException("Caller's account is not an active user.");
        }

        return userId;
    }
}
