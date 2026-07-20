namespace Vlms.Domain;

/// <summary>
/// The application's own user/profile record — <see cref="Role"/>-bearing (via <see cref="Roles"/>)
/// and the target of every existing FK that models "which user did/owns this"
/// (<c>Student.AssignedTeacherUserId</c>, <c>ParentGuardian.AppUserId</c>,
/// <c>LessonChangeProposal.ProposedByUserId</c>, etc. — adr/0002-roles-as-application-claims.md).
///
/// **Identity coexistence (STATE.md "Identity" item, replacing Microsoft Entra External ID with
/// self-rolled ASP.NET Core Identity) — the judgement call, recorded here because this type is
/// where it bites:** this class deliberately stays a plain Vlms.Domain POCO — it does NOT inherit
/// <c>Microsoft.AspNetCore.Identity.IdentityUser&lt;int&gt;</c>. ASP.NET Core Identity's own
/// credential/store row is a *separate* type, <c>Vlms.Infrastructure.Identity.VlmsIdentityUser</c>
/// (Infrastructure, not Domain), sharing this row's <see cref="Id"/> value as both its own primary
/// key and a foreign key back to <see cref="AppUser"/> (see <c>VlmsDbContext.OnModelCreating</c>'s
/// <c>AppUser</c> block, and <c>VlmsIdentityUser</c>'s own doc comment). Two alternatives were
/// considered and rejected:
/// - **`AppUser : IdentityUser&lt;int&gt;`** — would work, but drags
///   <c>Microsoft.AspNetCore.Identity</c>'s package tree into Vlms.Domain, which has been a
///   zero-NuGet-dependency project throughout this codebase's history (the same reason
///   <see cref="ICurrentUserContext"/>/<see cref="IBlobStorage"/>/<see cref="INotificationService"/>
///   are technology-agnostic interfaces here with real implementations in Vlms.Infrastructure) — and
///   every Domain entity below that references <c>AppUser</c> would transitively pull that
///   dependency in too. Rejected on that architectural-consistency ground alone.
/// - **An independent surrogate key on the Identity row, referenced from `AppUser` by an ordinary
///   nullable FK column** — the more common shape for "separate Identity table with an FK" — was
///   rejected in favour of a *shared* primary key (same int value, not merely a referencing column)
///   specifically because every existing FK throughout this schema
///   (`Student.AssignedTeacherUserId`, `ParentGuardian.AppUserId`, ...) already treats
///   <c>AppUser.Id</c> as *the* stable identifier for a user — a separate surrogate key on the
///   Identity side would need its own FK column here and would leave `AppUser.Id` and "the user's
///   real identity key" as two different numbers to keep straight for no benefit. A shared key needs
///   no new column and no schema change to any of those FKs at all.
///
/// The old `EntraObjectId` string property (find-by-external-federated-id) is gone entirely — under
/// local Identity there is no external id to look up by; <see cref="Id"/> itself is resolved directly
/// from the signed-in principal's <c>ClaimTypes.NameIdentifier</c> claim (Identity's own standard
/// user-id claim — see <c>Security/IdentityCurrentUserContext.cs</c>).
///
/// <see cref="Id"/> stays <c>ValueGeneratedNever</c> (VlmsDbContext.OnModelCreating), unchanged from
/// before — but its *source* changes: it used to be application-computed (`Max(Id)+1`, in the
/// now-removed `UserProvisioningService`); it is now always copied from the
/// <c>VlmsIdentityUser</c> row's own store-generated id, immediately after that row is created
/// (see <c>Identity/AppUserProvisioningService.cs</c>, the one place that pairing is enforced).
/// </summary>
public sealed class AppUser
{
    public required int Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }

    public ICollection<UserRole> Roles { get; init; } = new List<UserRole>();
}
