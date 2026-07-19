using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// Consent/DBS management UI (STATE.md, docs/design/data-design.md's <see cref="ConsentRecord"/>/
/// <see cref="ConsentSensitiveDetails"/> split, adr/0004-sensitive-data-access-control.md).
///
/// Authorization: role-checked inside the service itself (defense in depth, same pattern as
/// <see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/>/
/// <see cref="Vlms.Infrastructure.Guardianship.GuardianLinkService"/>) — restricted to Admin or
/// SafeguardingOfficer. This mirrors data-design.md's own wording for who may act on a
/// <see cref="ConsentRecord"/> ("Approved by Safeguarding Officer or Admin only") and is a hard
/// constraint (CLAUDE.md Project Law): the Approver role is curriculum-only and never conflated
/// with safeguarding/consent sign-off, so it has no access here, and there is no Parent
/// self-service path in this codebase yet (the Parent dashboard/self-submission flow is STATE.md
/// Next item 1, not built) — this service is the Admin/SafeguardingOfficer-operated equivalent of
/// entering what a parent has already told/given the programme in writing (e.g. from a paper
/// consent form), not a simulation of parent self-service.
///
/// <see cref="RecordAsync"/> creates the <see cref="ConsentRecord"/> and its 1:1
/// <see cref="ConsentSensitiveDetails"/> row together in a single <c>SaveChangesAsync</c> call
/// (both ids are computed up front, since this codebase's ids are application-assigned — see
/// VlmsDbContext.OnModelCreating — so both entities can be added to the change tracker before any
/// database round-trip) — atomic by construction, not by an explicit transaction, avoiding the
/// non-atomicity class of bug a prior increment's checker review found and fixed
/// (StudentRegistrationService, commit d2f9c63). A new <see cref="ConsentRecord"/> starts Pending;
/// <see cref="DecideAsync"/> is the only path that sets Approved/Rejected (mirrors
/// <see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/>'s propose/decide shape),
/// recording <see cref="ConsentRecord.ApprovedByUserId"/> as the deciding caller.
///
/// Never touches <see cref="ConsentSensitiveDetails"/> via <c>IgnoreQueryFilters()</c> or raw SQL —
/// all reads/writes go through the normal <see cref="VlmsDbContext"/>, so the existing whole-entity
/// query filter and <see cref="Auditing.SensitiveDataAuditInterceptor"/> read-audit apply exactly as
/// they do anywhere else in this codebase.
/// </summary>
public sealed class ConsentRecordService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public ConsentRecordService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Records a new annual <see cref="ConsentRecord"/> (Status = Pending) plus its
    /// <see cref="ConsentSensitiveDetails"/> for the given student. Admin/SafeguardingOfficer only.
    /// </summary>
    public async Task<ConsentRecord> RecordAsync(
        int studentId,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly expiryDate,
        bool photoMediaConsent,
        bool transportOffsiteConsent,
        bool dataSharingConsent,
        int submittedByParentId,
        string emergencyContact,
        string? emergencyMedicalInfo,
        string? dietarySEN,
        CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();
        ArgumentException.ThrowIfNullOrWhiteSpace(emergencyContact);

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        _ = await _db.ParentGuardians.SingleAsync(p => p.Id == submittedByParentId, ct);

        var recordId = await NextId(_db.ConsentRecords, ct);
        var record = new ConsentRecord
        {
            Id = recordId,
            StudentId = studentId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PhotoMediaConsent = photoMediaConsent,
            TransportOffsiteConsent = transportOffsiteConsent,
            DataSharingConsent = dataSharingConsent,
            Status = ConsentStatus.Pending,
            SubmittedByParentId = submittedByParentId,
            ExpiryDate = expiryDate
        };
        _db.ConsentRecords.Add(record);

        var detailsId = await NextId(_db.ConsentSensitiveDetails, ct);
        _db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = detailsId,
            ConsentRecordId = recordId,
            EmergencyContact = emergencyContact,
            EmergencyMedicalInfo = emergencyMedicalInfo,
            DietarySEN = dietarySEN
        });

        await _db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>
    /// Approves or rejects an existing (Pending, or previously decided) <see cref="ConsentRecord"/>.
    /// Admin/SafeguardingOfficer only — never the Approver role (curriculum-only).
    /// </summary>
    public async Task<ConsentRecord> DecideAsync(int consentRecordId, bool approve, CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();
        var approverUserId = RequireResolvedUserId();

        var record = await _db.ConsentRecords.SingleAsync(c => c.Id == consentRecordId, ct);

        _db.Entry(record).CurrentValues.SetValues(new ConsentRecord
        {
            Id = record.Id,
            StudentId = record.StudentId,
            PeriodStart = record.PeriodStart,
            PeriodEnd = record.PeriodEnd,
            PhotoMediaConsent = record.PhotoMediaConsent,
            TransportOffsiteConsent = record.TransportOffsiteConsent,
            DataSharingConsent = record.DataSharingConsent,
            Status = approve ? ConsentStatus.Approved : ConsentStatus.Rejected,
            SubmittedByParentId = record.SubmittedByParentId,
            ApprovedByUserId = approverUserId,
            ExpiryDate = record.ExpiryDate
        });

        await _db.SaveChangesAsync(ct);
        return record;
    }

    private void RequireAdminOrSafeguardingOfficer()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to manage consent records.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<ConsentRecord> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;

    private static async Task<int> NextId(DbSet<ConsentSensitiveDetails> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
