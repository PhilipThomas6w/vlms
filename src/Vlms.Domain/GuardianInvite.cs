namespace Vlms.Domain;

/// <summary>
/// A single-use, expiring invitation letting a <see cref="ParentGuardian"/> who does not yet have
/// an <see cref="AppUser"/> account create one and become the parent-login identity for a specific
/// <see cref="Student"/> pairing — the self-rolled replacement for the Microsoft Graph
/// "invitation already scoped to the correct Student record" mechanism named in
/// `docs/design/data-design.md`'s "Guardian link verification" section (STATE.md, retiring the
/// former Entra-tenant blocker per adr/0005-hosting-pivot-self-hosted-vps-postgresql.md).
///
/// **When one is issued (the precise condition, not "new vs existing guardian" — see
/// `Guardianship/GuardianInviteService.cs`):** whenever a <see cref="StudentGuardianLink"/> is
/// created for a <see cref="ParentGuardian"/> whose <see cref="ParentGuardian.AppUserId"/> is still
/// null. A guardian who already has an account (linked to an earlier child) needs no new invite at
/// all when a second child is linked — <see cref="Vlms.Infrastructure.Authorization.ParentGuardianLinkage"/>'s
/// existing join already grants them access to the new <see cref="StudentGuardianLink"/> row the
/// moment it exists, via their one existing <see cref="ParentGuardian.AppUserId"/>.
///
/// **"Signed"**: the emailed link does not embed this row's <see cref="Id"/> in the clear — it
/// carries an ASP.NET Core Data Protection <c>ITimeLimitedDataProtector</c>-protected token wrapping
/// it (authenticated encryption — tampering is detected, not just discouraged), generated fresh by
/// <c>GuardianInviteService.IssueAsync</c> and never persisted itself (only this row is). See that
/// service's doc comment for the exact API and why it was chosen over a hand-rolled HMAC scheme.
///
/// **"Single-use"**: enforced here, not solely by the token — <see cref="ConsumedAt"/> starts null
/// and is set exactly once, by <c>GuardianInviteService.AcceptAsync</c>, inside the same transaction
/// that creates the new account and sets <see cref="ParentGuardian.AppUserId"/>. A second attempt
/// with the same (still cryptographically valid, since Data Protection's own expiry window hasn't
/// necessarily passed) token is rejected once <see cref="ConsumedAt"/> is non-null. A small
/// check-then-mark race under concurrent double-use is accepted, not engineered away — the same
/// documented "race-prone but acceptable at tens-of-users scale" judgement this codebase already
/// made for `UserProvisioningService`'s (now `AppUserProvisioningService`'s) `Max(Id)+1` id
/// generation.
///
/// **"Expiring"**: <see cref="ExpiresAt"/> is checked both by the Data Protection protector itself
/// (built-in expiry) and again here as a defence-in-depth belt-and-braces check, in case a future
/// caller ever unprotects a token without going through <c>GuardianInviteService</c>.
/// </summary>
public sealed class GuardianInvite : IChapterOwned
{
    /// <summary>The chapter this record belongs to (docs/adr/0010-chapters.md): always its parent row's. Left at 0, it is copied from the parent on save.</summary>
    public int ChapterId { get; init; }

    public required int Id { get; init; }
    public required int StudentId { get; init; }
    public required int ParentGuardianId { get; init; }
    public required int CreatedByUserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Null until accepted. Set exactly once by <c>GuardianInviteService.AcceptAsync</c>.</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>The <see cref="AppUser"/> created when this invite was accepted. Null until consumed.</summary>
    public int? ConsumedByAppUserId { get; init; }

    public Student? Student { get; init; }
    public ParentGuardian? ParentGuardian { get; init; }
    public AppUser? CreatedByUser { get; init; }
    public AppUser? ConsumedByAppUser { get; init; }
}
