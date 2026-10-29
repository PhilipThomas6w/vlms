using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// Real ASP.NET Core Identity-backed <see cref="ICurrentUserContext"/> (STATE.md "Identity" item,
/// replacing the former Entra External ID-backed <c>EntraCurrentUserContext</c>): resolves the
/// signed-in <see cref="AppUser"/> (by the caller's <c>ClaimTypes.NameIdentifier</c> claim — Identity's
/// own standard user-id claim, which *is* the shared <see cref="AppUser"/>/<see cref="VlmsIdentityUser"/>
/// id, no join needed to find it), its <see cref="UserRole"/> rows, and its
/// <see cref="VlmsIdentityUser.TwoFactorEnabled"/> status, for use by <see cref="VlmsDbContext"/>'s
/// sensitive-data query filters and the role/resource-based authorization handlers
/// (adr/0002-roles-as-application-claims.md, adr/0004-sensitive-data-access-control.md).
///
/// **The interactive-render-mode DI-scoping shape from the Entra era is retained unchanged, not
/// reinvented — verified, not assumed, that it still applies:** the officially documented pattern
/// for ASP.NET Core Identity in a Blazor Web App with Server interactivity
/// (learn.microsoft.com/aspnet/core/blazor/security/server) uses
/// <c>IdentityRevalidatingAuthenticationStateProvider</c> — a subclass of
/// <c>RevalidatingServerAuthenticationStateProvider</c>, itself a subclass of the same
/// <c>ServerAuthenticationStateProvider</c> whose "throws until a component has primed it" behaviour
/// motivated <c>AuthenticationStatePrincipalResolver</c>/the deferred-resolution shape in the first
/// place (see that class's doc comment and STATE.md's log for the original c318bc5/d2adf82
/// checker round-trip that found and fixed it under Entra). Nothing about that framework behaviour
/// is Entra-specific — it is Blazor Server's own circuit lifecycle — so the exact same architecture
/// this codebase already hardened is reused here: two constructors, and the
/// <see cref="AuthenticationStateProvider"/> one defers resolution to each read of
/// <see cref="UserId"/>/<see cref="HasRole"/>/<see cref="HasMfaEnabled"/>, never at construction.
///
/// Looks up AppUser/UserRole/VlmsIdentityUser via a short-lived <see cref="VlmsDbContext"/> of its
/// own, constructed from <see cref="DbContextOptions{TContext}"/> (never the DI-resolved
/// <see cref="VlmsDbContext"/> instance) — the same pattern
/// <see cref="Auditing.SensitiveDataAuditInterceptor"/> uses, and for the same reason: this class is
/// itself a constructor dependency of <see cref="VlmsDbContext"/>, so it cannot also depend on a
/// DI-resolved <see cref="VlmsDbContext"/> without a circular resolution. Handing the lookup context
/// <see cref="NullCurrentUserContext.Instance"/> is safe and inert (nothing queried here carries a
/// sensitive-data query filter itself — AppUser/UserRole/VlmsIdentityUser are all unrestricted).
///
/// **Re-read on every check, never cached for the scope (functional.md FR-016, Philip 22/10/2026).**
/// Until FR-016 this class resolved the caller once, through a <see cref="Lazy{T}"/>, and kept the
/// result for the DI scope. In an interactive Blazor Server circuit the scope lives as long as the
/// circuit, so a role an Admin granted or removed, or a 2FA disable, did not take effect in pages the
/// user already had open until they reconnected. Now every read of <see cref="UserId"/>,
/// <see cref="HasRole"/> and <see cref="HasMfaEnabled"/> re-resolves the principal (cheap: the
/// provider returns an already-completed task) and runs one small query against the current
/// <see cref="UserRole"/>/<see cref="AppUser"/>/<see cref="VlmsIdentityUser"/> rows, so every
/// authorization check and every query-filter evaluation (EF Core evaluates the filter's
/// <see cref="ICurrentUserContext"/> members once per query execution) sees the roles and MFA status
/// as they are at that moment. Deliberately no per-request or per-render cache: there is no request
/// boundary inside a circuit to tie one to, and the cost (one indexed primary-key query per check, a
/// handful per page) is small for this application. Role changes also rotate the user's security
/// stamp (<see cref="SecurityStamps"/>), so their sessions are re-validated; that is a second layer,
/// not what makes the change immediate.
///
/// <b>Chapters (docs/adr/0010-chapters.md).</b> <see cref="ActiveChapterId"/> is the chapter the
/// caller last chose with the chapter switcher (<see cref="ActiveChapterChoice"/>) if they still hold
/// a role there, otherwise the lowest-numbered chapter they hold a role in; it is worked out again on
/// every read, like the roles, so a removed role or a switch takes effect on the next action. A
/// chapter role counts in <see cref="HasRole"/> only when held in that chapter. While a
/// <see cref="ChapterScopeOverride"/> is entered (guardian invite acceptance only), the active
/// chapter is the invite's.
/// </summary>
public sealed class IdentityCurrentUserContext : ICurrentUserContext
{
    private readonly Func<ClaimsPrincipal> _principal;
    private readonly DbContextOptions<VlmsDbContext> _dbContextOptions;
    private readonly ChapterScopeOverride? _scopeOverride;

    public IdentityCurrentUserContext(
        ClaimsPrincipal principal, DbContextOptions<VlmsDbContext> dbContextOptions, ChapterScopeOverride? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _principal = () => principal;
        _dbContextOptions = dbContextOptions;
        _scopeOverride = scopeOverride;
    }

    /// <summary>
    /// Defers <see cref="AuthenticationStatePrincipalResolver.Resolve"/> to each read of
    /// <see cref="UserId"/>/<see cref="HasRole"/>/<see cref="HasMfaEnabled"/> — construction itself
    /// never calls <paramref name="authenticationStateProvider"/>. This is the one Vlms.Web's
    /// Program.cs uses. See the class doc comment for why this shape is unchanged from the Entra
    /// era rather than reinvented.
    /// </summary>
    public IdentityCurrentUserContext(
        AuthenticationStateProvider authenticationStateProvider, DbContextOptions<VlmsDbContext> dbContextOptions,
        ChapterScopeOverride? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(dbContextOptions);

        _principal = () => AuthenticationStatePrincipalResolver.Resolve(authenticationStateProvider);
        _dbContextOptions = dbContextOptions;
        _scopeOverride = scopeOverride;
    }

    /// <summary>
    /// The caller's <see cref="AppUser"/> id, or null when the principal carries no integer user-id
    /// claim or no <see cref="AppUser"/> row exists for it (a caller is never trusted on the claim's
    /// say-so alone). Re-read on every access.
    /// </summary>
    public int? UserId
    {
        get
        {
            if (ClaimedUserId() is not int userId)
            {
                return null;
            }

            using var db = NewLookupContext();
            return db.AppUsers.Any(u => u.Id == userId) ? userId : null;
        }
    }

    /// <summary>
    /// Whether the caller holds <paramref name="role"/> right now. Re-read on every call. Once an
    /// account has left, its Teacher, Admin and Safeguarding Officer rows no longer count
    /// (<see cref="LeftStaffPolicy"/>, FR-017), so a still-open session loses every staff page,
    /// staff service and staff route of the sensitive-data query filters the moment the account is
    /// marked as left; a Parent role it also holds still counts. A chapter role counts only when held
    /// in <see cref="ActiveChapterId"/> (docs/adr/0010-chapters.md); a national role has no chapter.
    /// </summary>
    public bool HasRole(Role role)
    {
        if (ClaimedUserId() is not int userId)
        {
            return false;
        }

        // UserRole.UserId is a foreign key to AppUser, so a matching row also proves the AppUser exists.
        using var db = NewLookupContext();
        if (role.IsNationalRole())
        {
            return db.UserRoles.Any(r => r.UserId == userId && r.Role == role && r.ChapterId == null);
        }

        if (ActiveChapter(db, userId) is not int chapterId)
        {
            return false;
        }

        return db.UserRoles.Any(r => r.UserId == userId && r.Role == role && r.ChapterId == chapterId)
            && !ChapterMembership.IsInactiveInChapter(db, userId, chapterId);
    }

    /// <summary>
    /// The chapter the caller is working in, re-read on every access (see the class doc comment).
    /// Null for an anonymous caller or one with no chapter role that still counts.
    /// </summary>
    public int? ActiveChapterId
    {
        get
        {
            if (_scopeOverride?.ChapterId is int overridden)
            {
                return overridden;
            }

            if (ClaimedUserId() is not int userId)
            {
                return null;
            }

            using var db = NewLookupContext();
            return ChapterMembership.ResolveActiveChapter(db, userId);
        }
    }

    /// <summary>Never true for a signed-in person (only <see cref="SystemCurrentUserContext"/>).</summary>
    public bool SeesAllChapters => false;

    private int? ActiveChapter(VlmsDbContext db, int userId) =>
        _scopeOverride?.ChapterId ?? ChapterMembership.ResolveActiveChapter(db, userId);

    /// <summary>Whether the caller has TOTP MFA enabled right now. Re-read on every access.</summary>
    public bool HasMfaEnabled
    {
        get
        {
            if (ClaimedUserId() is not int userId)
            {
                return false;
            }

            // Both rows of the shared-primary-key pair must exist (AppUser's doc comment).
            using var db = NewLookupContext();
            return db.AppUsers.Any(a => a.Id == userId)
                && db.Users.Any(u => u.Id == userId && u.TwoFactorEnabled);
        }
    }

    /// <summary>
    /// Whether the caller holds a staff role that still counts in any chapter, whatever the active
    /// chapter (docs/adr/0010-chapters.md assumption 9, amended 27/10/2026). Re-read on every access,
    /// so it holds straight after sign-in and after every chapter switch. Ignores
    /// <see cref="ChapterScopeOverride"/>, which changes only the active chapter.
    /// </summary>
    public bool HoldsStaffRoleInAnyChapter
    {
        get
        {
            if (ClaimedUserId() is not int userId)
            {
                return false;
            }

            using var db = NewLookupContext();
            return LeftStaffPolicy.HoldsCountingStaffRoleInAnyChapter(db, userId);
        }
    }

    // ClaimTypes.NameIdentifier is Identity's own standard user-id claim (set by the default
    // IUserClaimsPrincipalFactory<VlmsIdentityUser> on sign-in) — its value is the shared
    // AppUser/VlmsIdentityUser id directly, not an external identifier requiring a lookup join
    // (unlike the Entra object-id claim it replaces).
    private int? ClaimedUserId() =>
        int.TryParse(_principal().FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private VlmsDbContext NewLookupContext() => new(_dbContextOptions, NullCurrentUserContext.Instance);
}
