using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Safeguarding;

/// <summary>
/// Consent/DBS management UI (STATE.md, docs/requirements/functional.md FR-002,
/// docs/design/data-design.md's <see cref="DbsCheck"/>, adr/0004-sensitive-data-access-control.md).
///
/// Authorization: role-checked inside the service itself (defense in depth, same pattern as
/// <see cref="ConsentRecordService"/>) — restricted to Admin or SafeguardingOfficer, matching
/// FR-002 exactly ("Whole-record access restricted to Admin and Safeguarding Officer only —
/// Teacher and Approver have no access"). The Approver role is curriculum-only and never conflated
/// with safeguarding sign-off (CLAUDE.md Project Law) — it has no access here, same as Teacher.
///
/// <see cref="RecordAsync"/> validates the target <see cref="AppUser"/> actually holds
/// <see cref="Role.Teacher"/> (data-design.md: "DBS check tracking for teachers") before creating
/// the row. <see cref="UpdateStatusAsync"/> updates an existing check's <see cref="DbsCheckStatus"/>
/// (e.g. Pending -> Clear/Flagged once a result comes back).
///
/// Never touches <see cref="DbsCheck"/> via <c>IgnoreQueryFilters()</c> or raw SQL — all reads/
/// writes go through the normal <see cref="VlmsDbContext"/>, so the existing whole-entity query
/// filter and <see cref="Auditing.SensitiveDataAuditInterceptor"/> read-audit apply exactly as they
/// do anywhere else in this codebase.
/// </summary>
public sealed class DbsCheckService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;

    public DbsCheckService(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Records a new <see cref="DbsCheck"/> for a teacher. Admin/SafeguardingOfficer only. Throws
    /// if <paramref name="teacherUserId"/> doesn't exist or doesn't hold <see cref="Role.Teacher"/>.
    /// </summary>
    public async Task<DbsCheck> RecordAsync(
        int teacherUserId,
        DateOnly checkDate,
        DateOnly expiryDate,
        string certificateNumber,
        DbsCheckStatus status,
        CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateNumber);

        _ = await _db.AppUsers.SingleAsync(u => u.Id == teacherUserId, ct);

        var isTeacher = await _db.UserRoles.AnyAsync(r => r.UserId == teacherUserId && r.Role == Role.Teacher, ct);
        if (!isTeacher)
        {
            throw new InvalidOperationException(
                $"AppUser {teacherUserId} does not hold the Teacher role — DBS checks are tracked against teachers only (data-design.md).");
        }

        var check = new DbsCheck
        {
            Id = await NextId(_db.DbsChecks, ct),
            TeacherUserId = teacherUserId,
            CheckDate = checkDate,
            ExpiryDate = expiryDate,
            CertificateNumber = certificateNumber,
            Status = status
        };

        _db.DbsChecks.Add(check);
        await _db.SaveChangesAsync(ct);
        return check;
    }

    /// <summary>
    /// Updates an existing <see cref="DbsCheck"/>'s <see cref="DbsCheckStatus"/> (e.g. once a
    /// pending check's result comes back). Admin/SafeguardingOfficer only.
    /// </summary>
    public async Task<DbsCheck> UpdateStatusAsync(int dbsCheckId, DbsCheckStatus newStatus, CancellationToken ct = default)
    {
        RequireAdminOrSafeguardingOfficer();

        var check = await _db.DbsChecks.SingleAsync(d => d.Id == dbsCheckId, ct);

        _db.Entry(check).CurrentValues.SetValues(new DbsCheck
        {
            Id = check.Id,
            TeacherUserId = check.TeacherUserId,
            CheckDate = check.CheckDate,
            ExpiryDate = check.ExpiryDate,
            CertificateNumber = check.CertificateNumber,
            Status = newStatus
        });

        await _db.SaveChangesAsync(ct);
        return check;
    }

    private void RequireAdminOrSafeguardingOfficer()
    {
        if (!_currentUser.HasRole(Role.Admin) && !_currentUser.HasRole(Role.SafeguardingOfficer))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin or SafeguardingOfficer role to manage DBS check records.");
        }
    }

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<DbsCheck> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
