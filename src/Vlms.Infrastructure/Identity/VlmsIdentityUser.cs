using Microsoft.AspNetCore.Identity;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity's own credential/store row (password hash, security stamp, TOTP secret —
/// held in the base class's <c>IdentityUserToken&lt;int&gt;</c> table — <see cref="IdentityUser{TKey}.TwoFactorEnabled"/>,
/// lockout fields) — STATE.md "Identity" item, replacing Microsoft Entra External ID with
/// self-rolled ASP.NET Core Identity.
///
/// Deliberately a *separate* type from <see cref="Vlms.Domain.AppUser"/>, not <c>AppUser</c> itself
/// inheriting <see cref="IdentityUser{TKey}"/> — see <c>AppUser</c>'s own doc comment for the full
/// reasoning (keeping Vlms.Domain free of any ASP.NET Core package dependency, matching
/// <see cref="Vlms.Domain.ICurrentUserContext"/>/<see cref="Vlms.Domain.IBlobStorage"/>/
/// <see cref="Vlms.Domain.INotificationService"/>'s existing Domain-interface/Infrastructure-
/// implementation split). This row and the corresponding <see cref="Vlms.Domain.AppUser"/> row
/// share the same <c>Id</c> value (a shared-primary-key one-to-one association — see
/// <c>VlmsDbContext.OnModelCreating</c>'s <c>AppUser</c> block), always created together by
/// <c>Identity/AppUserProvisioningService.cs</c>.
///
/// <c>Id</c> (inherited, <c>int</c>) is deliberately left at EF Core's default
/// <c>ValueGeneratedOnAdd</c> for this entity — the one place in this schema that is NOT
/// <c>ValueGeneratedNever</c> app-assigned — because ASP.NET Core Identity's own
/// <c>UserManager&lt;TUser&gt;.CreateAsync</c> is what inserts this row; letting the database
/// (Npgsql serial/identity column) generate its id is the natural, unmodified behaviour of
/// Identity's own store, and <see cref="Vlms.Domain.AppUser"/>'s row is then created immediately
/// afterwards with that same value copied in.
/// </summary>
public sealed class VlmsIdentityUser : IdentityUser<int>
{
    /// <summary>
    /// An Admin has turned this student sign-in off (functional.md FR-011, adr/0007, Philip
    /// 22/10/2026). A disabled account is not confirmed (<see cref="VlmsUserConfirmation"/>), so it
    /// can't sign in; disabling also rotates the security stamp, ending existing sessions. Only
    /// <see cref="StudentAccountService"/> sets or clears it, and a password reset leaves it alone.
    /// A dedicated flag, not a lockout, because a parent's or Admin's reset clears lockouts; and on
    /// this row rather than <see cref="Vlms.Domain.Student"/>, whose updates copy every property and
    /// would silently clear a field they didn't know about.
    /// </summary>
    public bool SignInDisabled { get; set; }
}
