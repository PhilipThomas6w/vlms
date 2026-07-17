namespace Vlms.Domain;

public sealed class ParentGuardian
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ContactInfo { get; init; }
    public required bool IsPrimary { get; init; }

    public ICollection<StudentGuardianLink> StudentLinks { get; init; } = new List<StudentGuardianLink>();
    public ICollection<ConsentRecord> SubmittedConsentRecords { get; init; } = new List<ConsentRecord>();
}
