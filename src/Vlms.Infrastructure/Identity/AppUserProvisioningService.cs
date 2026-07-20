using Microsoft.AspNetCore.Identity;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// Creates a new local ASP.NET Core Identity account and its paired <see cref="AppUser"/> row
/// together — the one place this codebase enforces the "shared primary key" pairing invariant (see
/// <see cref="AppUser"/>'s and <see cref="VlmsIdentityUser"/>'s doc comments). Used by both the
/// general <c>/Account/Register</c> flow and the guardian invite-accept flow
/// (<see cref="Guardianship.GuardianInviteService.AcceptAsync"/>) — the two places this codebase
/// creates an Identity account at all.
///
/// **Retires <c>Provisioning/UserProvisioningService</c>, does not adapt it — a deliberate
/// judgement call, recorded per STATE.md's instruction not to silently drop it:** the old service's
/// whole reason to exist was find-or-create against a *federated* OIDC claim on every successful
/// Entra sign-in (`OnTokenValidated`), where the account itself was created by Entra's own hosted
/// sign-up flow and this codebase only ever learned about it after the fact. Local ASP.NET Core
/// Identity has no equivalent federated-provisioning moment — every account is created by an explicit
/// action this codebase's own code drives end to end (a Register form submit, or an invite-accept
/// form submit), so there is nothing left to "find" — only "create". Re-purposing the old find-or-create
/// shape onto a create-only flow would have left dead code (the "find" branch could never be
/// exercised) pretending to be a rename.
///
/// **The property that must survive the swap, and does, per STATE.md's explicit instruction:** a
/// freshly created <see cref="AppUser"/> gets zero <see cref="UserRole"/> rows — deny-by-default.
/// This falls out trivially here (nothing below ever inserts a <see cref="UserRole"/>), the same as
/// it did in the old service; role assignment remains a separate, not-yet-built, Admin-only action
/// (unchanged gap, out of scope here — see STATE.md).
///
/// **Atomicity, including composition with a caller's own transaction:** <see cref="UserManager{TUser}.CreateAsync(TUser, string)"/>
/// inserts <see cref="VlmsIdentityUser"/> and saves; inserting the paired <see cref="AppUser"/> is a
/// second, separate <c>SaveChangesAsync</c> against the same DI-scoped <see cref="VlmsDbContext"/>
/// instance (Identity's <c>UserOnlyStore&lt;VlmsIdentityUser, VlmsDbContext, int&gt;</c>, registered
/// via <c>AddEntityFrameworkStores&lt;VlmsDbContext&gt;()</c> in Vlms.Web's Program.cs, shares the
/// exact same scoped context this class is constructed with — the same sharing this codebase already
/// relies on for <c>GuardianLinkService</c>/<c>StudentRegistrationService</c>). Both writes must not
/// be split by a partial failure — the exact non-atomicity bug class a checker round-trip already
/// found and fixed once in this codebase (`StudentRegistrationService`, STATE.md commit d3a7e0d).
/// <see cref="CreateAsync"/> checks <see cref="Microsoft.EntityFrameworkCore.DatabaseFacade.CurrentTransaction"/>
/// before deciding whether to open (and later commit) its own transaction: when called on its own
/// (the plain Register flow — the only write in that request), it manages one itself; when called
/// from inside a caller's already-open transaction (<see cref="Guardianship.GuardianInviteService.AcceptAsync"/>,
/// which also needs to set <c>ParentGuardian.AppUserId</c> and mark the invite consumed atomically
/// alongside account creation), it detects the ambient transaction and leaves committing/rolling it
/// back to that caller — the standard EF Core "compose transactional units" pattern (see
/// [Managing transactions](https://learn.microsoft.com/ef/core/saving/transactions), "Using external
/// DbTransactions") rather than a bespoke transaction-passing parameter.
/// </summary>
public sealed class AppUserProvisioningService
{
    private readonly UserManager<VlmsIdentityUser> _userManager;
    private readonly VlmsDbContext _db;

    public AppUserProvisioningService(UserManager<VlmsIdentityUser> userManager, VlmsDbContext db)
    {
        _userManager = userManager;
        _db = db;
    }

    /// <summary>
    /// Creates a new <see cref="VlmsIdentityUser"/> (email/password credential) and its paired,
    /// zero-role <see cref="AppUser"/>. Returns the <see cref="IdentityResult"/> from
    /// <see cref="UserManager{TUser}"/> unchanged (e.g. weak-password/duplicate-email failures are
    /// reported there, not thrown) — callers (Register.razor, the invite-accept page) render its
    /// <see cref="IdentityResult.Errors"/> back to the user.
    /// </summary>
    public async Task<(IdentityResult Result, VlmsIdentityUser? IdentityUser, AppUser? AppUser)> CreateAsync(
        string email, string password, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var identityUser = new VlmsIdentityUser
        {
            UserName = email,
            Email = email
        };

        // Ambient-transaction composition — see the class doc comment.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var result = await _userManager.CreateAsync(identityUser, password);
            if (!result.Succeeded)
            {
                // Nothing to roll back explicitly — CreateAsync's own SaveChangesAsync failed or the
                // store validated the input and never wrote anything. Returning here (not throwing)
                // matches UserManager's own "report via IdentityResult" convention. If we own the
                // transaction it's simply never committed (rolled back on Dispose in `finally`); if
                // we don't, the caller decides what an unsuccessful CreateAsync means for its own
                // transaction (GuardianInviteService.AcceptAsync — see its own doc comment).
                return (result, null, null);
            }

            var appUser = new AppUser
            {
                Id = identityUser.Id,
                DisplayName = displayName,
                Email = email
            };
            _db.AppUsers.Add(appUser);
            await _db.SaveChangesAsync(ct);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(ct);
            }

            return (result, identityUser, appUser);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
