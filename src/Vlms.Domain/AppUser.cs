namespace Vlms.Domain;

/// <summary>
/// The application's own user/profile record — <see cref="Role"/>-bearing (via <see cref="Roles"/>)
/// and the target of every existing FK that models "which user did/owns this"
/// (<c>Student.AssignedTeacherUserId</c>, <c>ParentGuardian.AppUserId</c>,
/// <c>LessonSuggestion.AuthorUserId</c>, etc. — adr/0002-roles-as-application-claims.md).
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

    /// <summary>
    /// STATE.md "Manual 'leaving' flag..." — set by the Admin-only
    /// <c>Retention.LeavingService.StepDownAsync</c>, feeding
    /// <c>Reporting.RetentionFlagging</c>'s "6 years after expiry <i>or the teacher leaving</i>"
    /// <see cref="DbsCheck"/> retention check (docs/governance/security-compliance.md). Mirrors
    /// <see cref="StudentLessonCompletion.IsReversed"/>/<see cref="StudentLessonCompletion.ReversedAt"/>'s
    /// shape (a bool plus its own captured timestamp) rather than folding both into one nullable
    /// field, for the same reason: an explicit boolean reads clearly at every call site, and the
    /// timestamp is only meaningful once the boolean is true.
    ///
    /// <b>Schema-placement judgement call, documented rather than left implicit:</b> this field lives
    /// directly on <see cref="AppUser"/> — generic to any <see cref="AppUser"/> row, not a new
    /// Teacher-specific entity — even though STATE.md's item only asks for a Teacher-leaving concept.
    /// This follows the precedent <see cref="DbsCheck"/> itself already set: there is no dedicated
    /// "Teacher" entity anywhere in this codebase, only an <see cref="AppUser"/> row that happens to
    /// hold a <see cref="UserRole"/> of <see cref="Role.Teacher"/> — <see cref="DbsCheck.TeacherUserId"/>
    /// already points at a plain <see cref="AppUser"/>, validated Teacher-role-holding at write time
    /// (<c>Safeguarding.DbsCheckService.RecordAsync</c>), not by type. Introducing a new entity just
    /// for "has this user left" would be inconsistent with that existing shape for no benefit — a
    /// generic field is simpler, and every other role (Admin/Approver/Parent/SafeguardingOfficer)
    /// leaving the organisation is an equally plausible future need this field costs nothing extra to
    /// also support. What *is* scoped is the action itself:
    /// <c>LeavingService.StepDownAsync</c> validates the target holds <see cref="Role.Teacher"/>,
    /// <see cref="Role.Admin"/> or <see cref="Role.SafeguardingOfficer"/> (Teacher only until FR-017
    /// was amended on 22/10/2026), and locks them out entirely (<c>Identity.LeftStaffPolicy</c>).
    /// Students and parents who leave keep access (FR-017); no write path sets this field on a
    /// parent-only or student account.
    ///
    /// Amended 27/10/2026 (FR-017): for staff this is <b>stepped down</b>. An Admin's
    /// <c>LeavingService.ReinstateAsync</c> clears it and <see cref="LeftAt"/> again, restoring their kept
    /// staff roles; each period is kept in <see cref="StaffStepDown"/>.
    /// </summary>
    public bool HasLeft { get; init; }

    /// <summary>See <see cref="HasLeft"/>. Null unless <see cref="HasLeft"/> is true: the current step-down date, which starts the retention clock.</summary>
    public DateTime? LeftAt { get; init; }

    public ICollection<UserRole> Roles { get; init; } = new List<UserRole>();
}
