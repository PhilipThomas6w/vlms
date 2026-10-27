using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Retention;

/// <summary>
/// The monthly purge of the read-audit log (docs/adr/0014-retention-purge.md decision 6): deletes
/// <see cref="SensitiveDataAccessLog"/> rows older than 6 years, and nothing else.
///
/// The context it is given must connect as the dedicated <c>vlms_retention</c> database role
/// (<c>ConnectionStrings:AuditLogPurge</c>, configured for the jobs service only), never as
/// <c>vlms_app</c>: <c>vlms_app</c> cannot delete from the table at all (adr/0004 pass 8), and
/// <c>vlms_retention</c>'s only row-level-security DELETE policy admits rows whose
/// <c>accessed_at</c> is more than 6 years ago (migration <c>AddDeletionLog</c>). The cutoff here and
/// the policy agree, so the policy is the database's own backstop, not the only check.
/// </summary>
public sealed class AuditLogPurgeService
{
    /// <summary>docs/governance/security-compliance.md: the audit log is kept 6 years.</summary>
    public const int RetentionYears = 6;

    /// <summary>The purge's own connection string, set for the jobs service only (<c>/etc/vlms/jobs.env</c>).</summary>
    public const string ConnectionStringName = "AuditLogPurge";

    private readonly VlmsDbContext _purgeDb;
    private readonly ICurrentUserContext _currentUser;

    public AuditLogPurgeService(VlmsDbContext purgeDb, ICurrentUserContext currentUser)
    {
        _purgeDb = purgeDb;
        _currentUser = currentUser;
    }

    /// <summary>Deletes rows accessed before <paramref name="now"/> minus 6 years; returns how many.</summary>
    public async Task<int> PurgeAsync(DateTime now, CancellationToken ct = default)
    {
        // The scheduled job's system principal only (no signed-in person).
        if (!_currentUser.SeesAllChapters || _currentUser.UserId is not null)
        {
            throw new UnauthorizedAccessException("Only the scheduled job purges the audit log.");
        }

        if (now.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The purge time must be UTC.", nameof(now));
        }

        var cutoff = now.AddYears(-RetentionYears);
        return await _purgeDb.SensitiveDataAccessLogs.Where(l => l.AccessedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
