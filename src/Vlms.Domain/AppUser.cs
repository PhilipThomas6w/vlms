namespace Vlms.Domain;

/// <summary>Maps to the Entra External ID identity.</summary>
public sealed class AppUser
{
    public required int Id { get; init; }
    public required string EntraObjectId { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }

    public ICollection<UserRole> Roles { get; init; } = new List<UserRole>();
}
