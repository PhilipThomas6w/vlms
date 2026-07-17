namespace Vlms.Domain;

public sealed class ParentGuardian
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ContactInfo { get; init; }
    public required bool IsPrimary { get; init; }

    /// <summary>
    /// The AppUser this ParentGuardian is the parent-login identity for (added during
    /// implementation to support self/parent login — see STATE.md and data-design.md). Null
    /// until the guardian has an Entra External ID sign-in provisioned; drives the Parent
    /// resource-based authorization check (a Parent may only act on a linked Student).
    /// </summary>
    public int? AppUserId { get; init; }

    public AppUser? AppUser { get; init; }
    public ICollection<StudentGuardianLink> StudentLinks { get; init; } = new List<StudentGuardianLink>();
    public ICollection<ConsentRecord> SubmittedConsentRecords { get; init; } = new List<ConsentRecord>();
}
