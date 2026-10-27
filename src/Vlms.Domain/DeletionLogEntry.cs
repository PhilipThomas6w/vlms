namespace Vlms.Domain;

/// <summary>
/// One row per record deleted under the retention rules (docs/adr/0014-retention-purge.md decision 4):
/// the evidence that retention is enforced. It holds only the record's type and id, when it was
/// deleted, who deleted it and the chapter it belonged to; never a name, date of birth or any other
/// personal data. Permanent: the app's database login may insert and read rows but never change or
/// delete them (migration <c>AddDeletionLog</c>: REVOKE plus row-level security, as for
/// <see cref="SensitiveDataAccessLog"/>).
/// </summary>
public sealed class DeletionLogEntry
{
    public const int MaxRecordTypeLength = 64;

    /// <summary>The record types written by <c>Retention.RetentionDeletionService</c>.</summary>
    public static class RecordTypes
    {
        public const string Student = "Student";
        public const string DbsCheck = "DbsCheck";
        public const string StudentApplication = "StudentApplication";
        public const string ParentGuardian = "ParentGuardian";
        public const string Account = "Account";
    }

    public int Id { get; init; }

    /// <summary>One of <see cref="RecordTypes"/>.</summary>
    public required string RecordType { get; init; }

    /// <summary>The deleted record's id (for <see cref="RecordTypes.Account"/>, the account's user id).</summary>
    public required int RecordId { get; init; }

    public required DateTime DeletedAt { get; init; }

    /// <summary>The Admin who deleted it. Deliberately not a foreign key: the log outlives their account.</summary>
    public required int DeletedByUserId { get; init; }

    /// <summary>The chapter the record belonged to. Not a foreign key, for the same reason.</summary>
    public required int ChapterId { get; init; }
}
