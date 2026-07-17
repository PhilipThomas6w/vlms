namespace Vlms.Domain;

public enum SensitiveAccessType
{
    View,
    Export
}

/// <summary>
/// Audit trail of reads (not just writes) of safeguarding data. Write-once — update/delete are
/// denied at the database permission level (SQL Server DENY on the application principal), not
/// just an application convention. Retained 6 years regardless of the referenced record's own
/// retention period (adr/0004-sensitive-data-access-control.md).
/// </summary>
public sealed class SensitiveDataAccessLog
{
    public required int Id { get; init; }

    /// <summary>Null when the accessing caller's identity could not be resolved (e.g. a background job).</summary>
    public int? UserId { get; init; }

    /// <summary>The entity type accessed — "DbsCheck" or "ConsentSensitiveDetails".</summary>
    public required string Entity { get; init; }

    public required int EntityId { get; init; }
    public required DateTime AccessedAt { get; init; }
    public required SensitiveAccessType AccessType { get; init; }
}
