using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Infrastructure.Progress;

/// <summary>The bytes of one certificate PDF and the file name to offer for it.</summary>
public sealed record CertificateFileContent(string FileName, byte[] Content);

/// <summary>
/// Read side of certificates (functional.md FR-010): resolves a <see cref="Certificate.Id"/> to its
/// PDF only if the current caller may see it. Allowed: the student the certificate belongs to
/// (<see cref="Student.AppUserId"/>), that student's linked parents who have not left, any Teacher,
/// any Admin (with MFA, as every Admin check in this codebase). Everyone else gets null: an unknown
/// id, a certificate the caller may not see and a missing blob are indistinguishable, so the
/// endpoint cannot be used to probe which certificates exist.
///
/// Does its own role and link checks rather than relying on the endpoint policy (defence in depth,
/// same as <c>LessonLibraryService</c>). The blob key never comes from the caller. For Student and
/// Parent callers a certificate whose completion has been reversed is treated as not found,
/// matching the parent dashboard (Philip's decision, 20/07/2026); Teachers and Admins still see it.
/// </summary>
public sealed class CertificateAccessService
{
    /// <summary>The roles the endpoint policy admits; the per-certificate checks below still apply.</summary>
    public static readonly IReadOnlyList<Role> CertificateRoles = [Role.Student, Role.Parent, Role.Teacher, Role.Admin];

    private readonly VlmsDbContext _db;
    private readonly IBlobStorage _blobStorage;
    private readonly ICurrentUserContext _currentUser;

    public CertificateAccessService(VlmsDbContext db, IBlobStorage blobStorage, ICurrentUserContext currentUser)
    {
        _db = db;
        _blobStorage = blobStorage;
        _currentUser = currentUser;
    }

    /// <summary>The certificate's PDF, or null if the id is unknown, the caller may not see it, or the blob is missing.</summary>
    public async Task<CertificateFileContent?> OpenAsync(int certificateId, CancellationToken ct = default)
    {
        if (_currentUser.UserId is not int userId)
        {
            return null;
        }

        var row = await _db.Certificates.AsNoTracking()
            .Where(c => c.Id == certificateId)
            .Join(_db.StudentLessonCompletions, c => c.StudentLessonCompletionId, slc => slc.Id,
                (c, slc) => new { c.Id, c.BlobKey, slc.StudentId, slc.IsReversed })
            .SingleOrDefaultAsync(ct);
        if (row is null || !await MayViewAsync(userId, row.StudentId, row.IsReversed, ct))
        {
            return null;
        }

        var content = await _blobStorage.ReadAsync(row.BlobKey, ct);
        return content is null ? null : new CertificateFileContent($"certificate-{row.Id}.pdf", content);
    }

    private async Task<bool> MayViewAsync(int userId, int studentId, bool isReversed, CancellationToken ct)
    {
        if (_currentUser.HasRole(Role.Teacher))
        {
            return true;
        }

        if (_currentUser.HasRole(Role.Admin) && _currentUser.HasMfaEnabled)
        {
            return true;
        }

        if (isReversed)
        {
            return false;
        }

        if (_currentUser.HasRole(Role.Student)
            && await _db.Students.AnyAsync(s => s.Id == studentId && s.AppUserId == userId, ct))
        {
            return true;
        }

        return _currentUser.HasRole(Role.Parent)
            // A staff leaver who is also a parent keeps parent access (FR-017, LeftStaffPolicy).
            && await _db.AppUsers.AnyAsync(u => u.Id == userId && (!u.HasLeft || u.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer)), ct)
            && await ParentGuardianLinkage.IsLinkedAsync(_db, studentId, userId, ct);
    }
}
