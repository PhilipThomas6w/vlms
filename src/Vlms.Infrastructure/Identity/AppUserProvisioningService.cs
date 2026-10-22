using Microsoft.AspNetCore.Identity;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// Creates a new local ASP.NET Core Identity account and its paired <see cref="AppUser"/> row
/// together — the one place this codebase enforces the "shared primary key" pairing invariant (see
/// <see cref="AppUser"/>'s and <see cref="VlmsIdentityUser"/>'s doc comments). Used by both the
/// general <c>/Account/Register</c> flow and the guardian invite-accept flow
/// (<see cref="Guardianship.GuardianInviteService.AcceptAsync"/>) — the two places this codebase
/// creates an adult Identity account — and, through <see cref="CreateStudentSignInAsync"/>, by
/// <see cref="StudentAccountService"/> for a parent-created student sign-in with no email (FR-011,
/// adr/0007-student-sign-in-accounts.md).
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
/// it did in the old service; role assignment is a separate, Admin-only action —
/// <see cref="RoleAssignmentService"/>, plus <see cref="FirstAdminBootstrapService"/> for the very
/// first Admin.
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
        string email, string password, string displayName, CancellationToken ct = default, bool emailConfirmed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        // An adult's username is its email, so it always contains '@'; a student username never
        // does (StudentUsername). Enforced here so the two can never collide (adr/0007).
        if (!email.Contains('@'))
        {
            throw new ArgumentException("An adult account's email address must contain '@'.", nameof(email));
        }

        var identityUser = new VlmsIdentityUser
        {
            UserName = email,
            Email = email,
            // True only when the caller already proved the address (the guardian invite link was
            // emailed to it); self-registration must confirm by email — see VlmsIdentityPolicy.
            EmailConfirmed = emailConfirmed
        };

        return await CreatePairAsync(identityUser, password, displayName, appUserEmail: email, ct);
    }

    /// <summary>
    /// Creates a student sign-in (functional.md FR-011, adr/0007-student-sign-in-accounts.md): a
    /// <see cref="VlmsIdentityUser"/> with a username and password and <b>no email address</b>, and
    /// its paired, zero-role <see cref="AppUser"/> (whose required <see cref="AppUser.Email"/> is the
    /// empty string: there is no address, and no email is ever sent to it). Only
    /// <see cref="StudentAccountService.CreateSignInAsync"/> calls this, inside its own transaction,
    /// which also grants <see cref="Role.Student"/> and sets <see cref="Student.AppUserId"/>; that
    /// link is what makes the account confirmed (<see cref="VlmsUserConfirmation"/>).
    /// <paramref name="username"/> must already meet <see cref="StudentUsername"/>'s rule; this
    /// throws otherwise, so an email-shaped or invalid username can never reach the store.
    /// </summary>
    public async Task<(IdentityResult Result, VlmsIdentityUser? IdentityUser, AppUser? AppUser)> CreateStudentSignInAsync(
        string username, string password, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!StudentUsername.IsValid(username))
        {
            throw new ArgumentException(StudentUsername.RuleDescription, nameof(username));
        }

        var identityUser = new VlmsIdentityUser
        {
            UserName = username,
            Email = null,
            EmailConfirmed = false
        };

        return await CreatePairAsync(identityUser, password, displayName, appUserEmail: "", ct);
    }

    private async Task<(IdentityResult Result, VlmsIdentityUser? IdentityUser, AppUser? AppUser)> CreatePairAsync(
        VlmsIdentityUser identityUser, string password, string displayName, string appUserEmail, CancellationToken ct)
    {
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
                Email = appUserEmail
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
