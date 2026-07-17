using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Provisioning;

/// <summary>
/// Finds or creates the <see cref="AppUser"/> matching a signed-in Microsoft Entra External ID
/// principal's object id. Invoked from the OIDC <c>OnTokenValidated</c> event wired in
/// Vlms.Web's Program.cs, so every successful sign-in provisions a row.
///
/// A newly created <see cref="AppUser"/> gets zero <see cref="UserRole"/> rows — deny-by-default:
/// a brand-new sign-in must not implicitly gain any access. Role assignment is a separate,
/// Admin-only action (out of scope for this service).
/// </summary>
public sealed class UserProvisioningService
{
    private readonly VlmsDbContext _db;

    public UserProvisioningService(VlmsDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the existing <see cref="AppUser"/> for <paramref name="entraObjectId"/>, or
    /// creates one (with no roles) if this is the first sign-in for that identity.
    /// </summary>
    public async Task<AppUser> FindOrCreateAsync(
        string entraObjectId, string displayName, string email, CancellationToken cancellationToken = default)
    {
        var existing = await _db.AppUsers
            .SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        // Ids are application-assigned (ValueGeneratedNever — VlmsDbContext.OnModelCreating)
        // rather than database identity, consistent with the rest of the model. Computing max+1
        // is race-prone under concurrent first-sign-ins, but this system is built for a
        // tens-of-users audience (VISION.md) where sign-up is not a high-concurrency path.
        var maxId = await _db.AppUsers.Select(u => (int?)u.Id).MaxAsync(cancellationToken) ?? 0;

        var newUser = new AppUser
        {
            Id = maxId + 1,
            EntraObjectId = entraObjectId,
            DisplayName = displayName,
            Email = email
        };

        _db.AppUsers.Add(newUser);
        await _db.SaveChangesAsync(cancellationToken);

        return newUser;
    }
}
