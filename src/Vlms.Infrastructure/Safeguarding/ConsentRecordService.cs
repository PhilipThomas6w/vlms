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
/// with safeguarding/consent sign-off, so it has no access here. Parents now submit the form
/// themselves (functional.md FR-015, <see cref="ParentConsentService"/>); <see cref="RecordAsync"/>
/// stays as the Admin/SafeguardingOfficer-operated equivalent for a paper form a parent has handed
/// in, and <see cref="DecideAsync"/> reviews both kinds of submission.
///
/// <b>Decision (FR-015, adr/0006-parent-consent-and-teacher-read-access.md):</b> only a Pending
/// submission can be decided; rejection requires comments; approval applies the form's personal
/// details (<see cref="ConsentRecord.StudentName"/>/<see cref="ConsentRecord.StudentDateOfBirth"/>)
/// to the <see cref="Student"/> (functional.md FR-013) in the same <c>SaveChangesAsync</c>.
/// Approval does not touch any earlier submission — an earlier Approved row stays as history.
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
        bool transportConsent,
        bool offsiteTripConsent,
        bool dataSharingConsent,
        int submittedByParentId,
        string emergencyContact,
        string? emergencyMedicalInfo,
        string? dietaryRequirements,
        string? specialEducationalNeeds,
        CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();
        var callerUserId = RequireResolvedUserId();
        ArgumentException.ThrowIfNullOrWhiteSpace(emergencyContact);
        ParentConsentService.RequireWithinTextLimit(dietaryRequirements, "Dietary requirements");
        ParentConsentService.RequireWithinTextLimit(specialEducationalNeeds, "Special educational needs");

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
            TransportConsent = transportConsent,
            OffsiteTripConsent = offsiteTripConsent,
            DataSharingConsent = dataSharingConsent,
            Status = ConsentStatus.Pending,
            SubmittedByParentId = submittedByParentId,
            ExpiryDate = expiryDate,
            SubmittedAt = DateTime.UtcNow,
            SubmittedByUserId = callerUserId
        };
        _db.ConsentRecords.Add(record);

        _db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = DetailsIdFor(recordId),
            ConsentRecordId = recordId,
            EmergencyContact = emergencyContact,
            EmergencyMedicalInfo = emergencyMedicalInfo,
            DietaryRequirements = dietaryRequirements,
            SpecialEducationalNeeds = specialEducationalNeeds
        });

        await _db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>
    /// Approves or rejects a Pending <see cref="ConsentRecord"/>. Admin/SafeguardingOfficer only —
    /// never the Approver role (curriculum-only). <paramref name="comments"/> is mandatory when
    /// rejecting (functional.md FR-015). Approving applies the submission's personal details to the
    /// <see cref="Student"/> (functional.md FR-013); an Approved, unexpired record is what
    /// <c>CompletionService</c>'s FR-003 consent check looks for.
    ///
    /// Updates go through <c>Entry(...).CurrentValues[...]</c> property by property (the entities
    /// are init-only), so a column added to either entity later is left alone rather than reset.
    /// </summary>
    public async Task<ConsentRecord> DecideAsync(
        int consentRecordId, bool approve, string? comments = null, CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();
        var deciderUserId = RequireResolvedUserId();

        if (!approve && string.IsNullOrWhiteSpace(comments))
        {
            throw new ArgumentException("Comments are required when rejecting a consent submission.", nameof(comments));
        }

        var record = await _db.ConsentRecords.SingleAsync(c => c.Id == consentRecordId, ct);
        if (record.Status != ConsentStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Consent record {consentRecordId} has already been {record.Status.ToString().ToLowerInvariant()} — only a pending submission can be decided.");
        }

        var recordValues = _db.Entry(record).CurrentValues;
        recordValues[nameof(ConsentRecord.Status)] = approve ? ConsentStatus.Approved : ConsentStatus.Rejected;
        recordValues[nameof(ConsentRecord.ApprovedByUserId)] = deciderUserId;
        recordValues[nameof(ConsentRecord.DecidedAt)] = DateTime.UtcNow;
        recordValues[nameof(ConsentRecord.DecisionComments)] = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();

        if (approve && (record.StudentName is not null || record.StudentDateOfBirth is not null))
        {
            var student = await _db.Students.SingleAsync(s => s.Id == record.StudentId, ct);
            var studentValues = _db.Entry(student).CurrentValues;
            if (record.StudentName is not null)
            {
                studentValues[nameof(Student.Name)] = record.StudentName;
            }

            if (record.StudentDateOfBirth is { } dateOfBirth)
            {
                studentValues[nameof(Student.DateOfBirth)] = dateOfBirth;
            }
        }

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

    /// <summary>
    /// A <see cref="ConsentSensitiveDetails"/> row takes its <see cref="ConsentRecord"/>'s own id
    /// (the two are strictly 1:1 and always created together). Since FR-015 a Parent creates these
    /// rows too, and a Parent's view of <see cref="ConsentSensitiveDetails"/> is confined by the
    /// query filter to their own children, so <c>MAX(id)+1</c> over that table would read too low
    /// and collide. <see cref="ConsentRecord"/> has no query filter, so its id is safe to derive
    /// from, and the unique <c>ConsentRecordId</c> index makes the derived id unique too.
    /// </summary>
    internal static int DetailsIdFor(int consentRecordId) => consentRecordId;
}
