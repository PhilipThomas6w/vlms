namespace Vlms.Domain;

/// <summary>
/// Resolves the current caller's identity, role(s), and MFA status for use by EF Core global query
/// filters and the sensitive-data read-audit interceptor (adr/0004-sensitive-data-access-control.md).
/// Vlms.Infrastructure provides three implementations: <c>IdentityCurrentUserContext</c> (real,
/// ASP.NET Core Identity-backed — STATE.md "Identity" item), <c>SystemCurrentUserContext</c> (for
/// <c>Vlms.Jobs</c>, no signed-in human caller), and <c>NullCurrentUserContext</c> (deny-by-default,
/// for design-time/migration tooling — never wired as the runtime context).
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>The current caller's <see cref="AppUser"/> id, or null if unresolved (e.g. a background job).</summary>
    int? UserId { get; }

    /// <summary>
    /// Whether the current caller holds the given role. A caller may hold more than one role. For a
    /// chapter role (<see cref="RoleScope.IsChapterRole"/>) this means "holds it in
    /// <see cref="ActiveChapterId"/>": a Teacher in one chapter who is Admin in another has no Admin
    /// powers while working in the first (docs/adr/0010-chapters.md). A national role is held or not.
    /// </summary>
    bool HasRole(Role role);

    /// <summary>
    /// The chapter the caller is working in (docs/adr/0010-chapters.md): every chapter role check
    /// and every chapter query filter uses it. Null when the caller holds no chapter role at all
    /// (an anonymous caller, a national-only account), who then sees no chapter's rows other than
    /// their own and their own children's.
    /// </summary>
    int? ActiveChapterId { get; }

    /// <summary>
    /// True only for the scheduled job's system principal, which sweeps every chapter. Never true
    /// for a signed-in person, national administrators included.
    /// </summary>
    bool SeesAllChapters { get; }

    /// <summary>
    /// Whether the current caller has TOTP multi-factor authentication enabled (ASP.NET Core
    /// Identity's <c>TwoFactorEnabled</c> on its own credential row — STATE.md "Identity" item,
    /// item 4). Consulted by <see cref="Vlms.Infrastructure.Authorization.MfaRequirementHandler"/>
    /// and by <c>VlmsDbContext</c>'s <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c> query filters
    /// (adr/0004-sensitive-data-access-control.md §"MFA-gated roles", pass 7): Admin/
    /// SafeguardingOfficer must have MFA enabled before either role "counts" for any authorization
    /// decision, page or query-filter — see <c>openwiki/authentication-authorization.md</c>'s MFA
    /// section for the full, documented scope decision (enforced for those two roles specifically,
    /// offered to everyone).
    /// </summary>
    bool HasMfaEnabled { get; }

    /// <summary>
    /// Whether the caller holds a Teacher, Admin or Safeguarding Officer role that still counts in
    /// <b>any</b> chapter, not just <see cref="ActiveChapterId"/> (docs/adr/0010-chapters.md
    /// assumption 9, amended by Philip 27/10/2026): one account has one password, so an account
    /// that is staff anywhere must use MFA whichever chapter it is working in, including one where
    /// it is only a Parent. A staff leaver's staff rows are history and do not count (FR-017).
    /// Consulted by <see cref="Vlms.Infrastructure.Authorization.MfaRequirementHandler"/>. The
    /// default covers only the active chapter, which is all a context that is not a signed-in
    /// person can know; <c>IdentityCurrentUserContext</c> checks every chapter.
    /// </summary>
    bool HoldsStaffRoleInAnyChapter =>
        HasRole(Role.Admin) || HasRole(Role.SafeguardingOfficer) || HasRole(Role.Teacher);
}
