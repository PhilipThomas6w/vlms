using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Chapters;

/// <summary>A chapter as listed on the chapter pages and in the chapter switcher.</summary>
public sealed record ChapterSummary(int Id, string Name, string Place, bool IsActive, DateTime CreatedAt);

/// <summary>A person holding a role, as listed on the national administration page (no chapter data).</summary>
public sealed record RoleHolder(int UserId, string DisplayName, string Email, bool HasLeft);

/// <summary>
/// Chapters (functional.md FR-021, docs/adr/0010-chapters.md): the chapter switcher for anyone who
/// belongs to more than one chapter, the list of open chapters a parent chooses from, and the
/// national administrator's powers. Those powers are deliberately narrow (least privilege, Philip
/// 24/10/2026): create and edit chapters, appoint and remove each chapter's Admins, and manage the
/// national roles (Approver, National Administrator). Nothing here reads or returns any chapter's
/// students, guardians, consent, DBS or application records; a national administrator sees those
/// only by also holding a role in that chapter.
///
/// Every national method requires <see cref="Role.NationalAdministrator"/> with TOTP MFA, checked
/// inside the service (defence in depth, as elsewhere). Role changes rotate the security stamp in
/// the same transaction (FR-016), as <see cref="RoleAssignmentService"/> does.
/// </summary>
public sealed class ChapterService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;

    public ChapterService(VlmsDbContext db, ICurrentUserContext currentUser, UserManager<VlmsIdentityUser> userManager)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
    }

    // ---------------------------------------------------------------- anyone signed in

    /// <summary>The open chapters, by name, for a parent choosing where to register a child.</summary>
    public async Task<IReadOnlyList<ChapterSummary>> ListOpenAsync(CancellationToken ct = default) =>
        await _db.Chapters.Where(c => c.IsActive).OrderBy(c => c.Name).Select(Summary).ToListAsync(ct);

    /// <summary>The chapters the caller belongs to (holds a counting chapter role in), by name.</summary>
    public async Task<IReadOnlyList<ChapterSummary>> ListMineAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is not int userId)
        {
            return [];
        }

        await using var db = LayoutContext();
        var ids = ChapterMembership.ChapterIds(db, userId);
        return await db.Chapters.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).Select(Summary).ToListAsync(ct);
    }

    /// <summary>The chapter the caller is working in, or null if they belong to none.</summary>
    public async Task<ChapterSummary?> GetActiveAsync(CancellationToken ct = default)
    {
        if (_currentUser.ActiveChapterId is not int chapterId)
        {
            return null;
        }

        await using var db = LayoutContext();
        return await db.Chapters.Where(c => c.Id == chapterId).Select(Summary).SingleOrDefaultAsync(ct);
    }

    // The chapter bar sits in the layout and loads while the page's own components query the
    // circuit's shared context, which allows one operation at a time ("A second operation was started
    // on this context"). Its two reads use a short-lived context of their own, with the same options
    // and the same caller (so the same query filters), as IdentityCurrentUserContext does.
    private VlmsDbContext LayoutContext() =>
        new((DbContextOptions<VlmsDbContext>)_db.GetService<IDbContextOptions>(), _currentUser);

    /// <summary>
    /// Makes <paramref name="chapterId"/> the caller's active chapter. Refused unless they hold a
    /// chapter role there that still counts; the choice is checked again on every request
    /// (<see cref="ChapterMembership.ResolveActiveChapter"/>), so a later loss of that role undoes it.
    /// </summary>
    public async Task SwitchToAsync(int chapterId, CancellationToken ct = default)
    {
        var userId = _currentUser.UserId
            ?? throw new UnauthorizedAccessException("Sign in to choose a chapter.");

        if (!ChapterMembership.ChapterIds(_db, userId).Contains(chapterId))
        {
            throw new UnauthorizedAccessException("You do not hold a role in that chapter.");
        }

        var existing = await _db.ActiveChapterChoices.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (existing is null)
        {
            _db.ActiveChapterChoices.Add(new ActiveChapterChoice { UserId = userId, ChapterId = chapterId });
        }
        else
        {
            _db.Entry(existing).CurrentValues[nameof(ActiveChapterChoice.ChapterId)] = chapterId;
        }

        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- national administrator: chapters

    /// <summary>Every chapter, open or not, by name. National administrator only.</summary>
    public async Task<IReadOnlyList<ChapterSummary>> ListAllAsync(CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        return await _db.Chapters.OrderBy(c => c.Name).Select(Summary).ToListAsync(ct);
    }

    /// <summary>Creates an open chapter. Names are unique. National administrator only.</summary>
    public async Task<ChapterSummary> CreateAsync(string name, string place, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        var (cleanName, cleanPlace) = Validate(name, place);
        await RequireUniqueNameAsync(cleanName, exceptId: null, ct);

        var chapter = new Chapter
        {
            Id = await _db.NextIdAsync<Chapter>(ct),
            Name = cleanName,
            Place = cleanPlace,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _db.Chapters.Add(chapter);
        await _db.SaveChangesAsync(ct);
        return new ChapterSummary(chapter.Id, chapter.Name, chapter.Place, chapter.IsActive, chapter.CreatedAt);
    }

    /// <summary>
    /// Renames a chapter or opens/closes it. Closing only stops new applications choosing it; its
    /// records and roles are untouched. National administrator only.
    /// </summary>
    public async Task UpdateAsync(int chapterId, string name, string place, bool isActive, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        var (cleanName, cleanPlace) = Validate(name, place);
        var chapter = await _db.Chapters.SingleOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter {chapterId} does not exist.");
        await RequireUniqueNameAsync(cleanName, exceptId: chapterId, ct);

        var values = _db.Entry(chapter).CurrentValues;
        values[nameof(Chapter.Name)] = cleanName;
        values[nameof(Chapter.Place)] = cleanPlace;
        values[nameof(Chapter.IsActive)] = isActive;
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- national administrator: chapter Admins

    /// <summary>The chapter's Admins (name and email only). National administrator only.</summary>
    public async Task<IReadOnlyList<RoleHolder>> ListAdminsAsync(int chapterId, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        return await _db.AppUsers
            .Where(u => u.Roles.Any(r => r.Role == Role.Admin && r.ChapterId == chapterId))
            .OrderBy(u => u.DisplayName)
            .Select(u => new RoleHolder(u.Id, u.DisplayName, u.Email, u.HasLeft))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Makes the account with this email an Admin of the chapter. The account must exist, have
    /// confirmed its email (so nobody who registered under someone else's address is appointed), not
    /// be a student sign-in, not have left, and not already be an Admin there. National administrator only.
    /// </summary>
    public async Task AppointAdminAsync(int chapterId, string email, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        if (!await _db.Chapters.AnyAsync(c => c.Id == chapterId, ct))
        {
            throw new InvalidOperationException($"Chapter {chapterId} does not exist.");
        }

        var user = await FindAppointableAsync(email, ct);
        if (await _db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.Role == Role.Admin && r.ChapterId == chapterId, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} is already an Admin of this chapter.");
        }

        await AddRoleAsync(user.Id, Role.Admin, chapterId, ct);
    }

    /// <summary>
    /// Removes the Admin role in this chapter. Refused for the chapter's last Admin who has not left
    /// (appoint another first), and for a leaver, whose staff role rows are kept as the record
    /// (FR-017). National administrator only.
    /// </summary>
    public async Task RemoveAdminAsync(int chapterId, int userId, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        var role = await _db.UserRoles.SingleOrDefaultAsync(r => r.UserId == userId && r.Role == Role.Admin && r.ChapterId == chapterId, ct)
            ?? throw new InvalidOperationException("That person is not an Admin of this chapter.");

        if (await _db.AppUsers.AnyAsync(u => u.Id == userId && u.HasLeft, ct))
        {
            throw new InvalidOperationException("This person has stepped down: their Admin role is kept for reinstatement and gives them nothing meanwhile.");
        }

        if (!await _db.AppUsers.AnyAsync(u => u.Id != userId && !u.HasLeft
                && u.Roles.Any(r => r.Role == Role.Admin && r.ChapterId == chapterId), ct))
        {
            throw new InvalidOperationException("This is the chapter's last Admin. Appoint another Admin first.");
        }

        await RemoveRoleAsync(role, ct);
    }

    // ---------------------------------------------------------------- national administrator: national roles

    /// <summary>Holders of a national role (Approver or National Administrator). National administrator only.</summary>
    public async Task<IReadOnlyList<RoleHolder>> ListNationalRoleHoldersAsync(Role role, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        RequireNationalRole(role);
        return await _db.AppUsers
            .Where(u => u.Roles.Any(r => r.Role == role && r.ChapterId == null))
            .OrderBy(u => u.DisplayName)
            .Select(u => new RoleHolder(u.Id, u.DisplayName, u.Email, u.HasLeft))
            .ToListAsync(ct);
    }

    /// <summary>Grants a national role by email, with the same account checks as appointing an Admin.</summary>
    public async Task GrantNationalRoleAsync(Role role, string email, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        RequireNationalRole(role);
        var user = await FindAppointableAsync(email, ct);
        if (await _db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.Role == role && r.ChapterId == null, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} already holds the {role.DisplayName()} role.");
        }

        await AddRoleAsync(user.Id, role, chapterId: null, ct);
    }

    /// <summary>Removes a national role. The last National Administrator cannot be removed.</summary>
    public async Task RevokeNationalRoleAsync(Role role, int userId, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        RequireNationalRole(role);
        var row = await _db.UserRoles.SingleOrDefaultAsync(r => r.UserId == userId && r.Role == role && r.ChapterId == null, ct)
            ?? throw new InvalidOperationException($"That person does not hold the {role.DisplayName()} role.");

        if (role == Role.NationalAdministrator
            && !await _db.UserRoles.AnyAsync(r => r.Role == Role.NationalAdministrator && r.UserId != userId, ct))
        {
            throw new InvalidOperationException("This is the last National Administrator. Grant the role to someone else first.");
        }

        await RemoveRoleAsync(row, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly System.Linq.Expressions.Expression<Func<Chapter, ChapterSummary>> Summary =
        c => new ChapterSummary(c.Id, c.Name, c.Place, c.IsActive, c.CreatedAt);

    private async Task<AppUser> FindAppointableAsync(string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Enter the person's email address.");
        }

        var normalised = email.Trim().ToLower();
        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Email != "" && u.Email.ToLower() == normalised, ct)
            ?? throw new InvalidOperationException("No account has that email address. Ask them to register first.");

        if (!await _db.Users.AnyAsync(u => u.Id == user.Id && u.EmailConfirmed, ct))
        {
            throw new InvalidOperationException($"{user.DisplayName} has not confirmed their email address yet.");
        }

        if (user.HasLeft)
        {
            throw new InvalidOperationException($"{user.DisplayName} has stepped down.");
        }

        return user;
    }

    private async Task AddRoleAsync(int userId, Role role, int? chapterId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.UserRoles.Add(new UserRole { UserId = userId, Role = role, ChapterId = chapterId });
        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, userId);
        await transaction.CommitAsync(ct);
    }

    private async Task RemoveRoleAsync(UserRole row, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.UserRoles.Remove(row);
        await _db.SaveChangesAsync(ct);
        await SecurityStamps.RotateAsync(_userManager, row.UserId);
        await transaction.CommitAsync(ct);
    }

    private static (string Name, string Place) Validate(string name, string place)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(place))
        {
            throw new ArgumentException("Enter the chapter's name and its place.");
        }

        var cleanName = name.Trim();
        var cleanPlace = place.Trim();
        if (cleanName.Length > Chapter.MaxNameLength || cleanPlace.Length > Chapter.MaxNameLength)
        {
            throw new ArgumentException($"Use {Chapter.MaxNameLength} characters or fewer.");
        }

        return (cleanName, cleanPlace);
    }

    private async Task RequireUniqueNameAsync(string name, int? exceptId, CancellationToken ct)
    {
        var lowered = name.ToLower();
        if (await _db.Chapters.AnyAsync(c => c.Name.ToLower() == lowered && (exceptId == null || c.Id != exceptId), ct))
        {
            throw new InvalidOperationException("Another chapter already has that name.");
        }
    }

    private static void RequireNationalRole(Role role)
    {
        if (!role.IsNationalRole())
        {
            throw new ArgumentException($"{role.DisplayName()} is held in a chapter; chapter Admins appoint it.");
        }
    }

    private void RequireNationalAdministrator()
    {
        if (!_currentUser.HasRole(Role.NationalAdministrator) || !_currentUser.HasMfaEnabled)
        {
            throw new UnauthorizedAccessException(
                "Only a National Administrator with two-factor authentication turned on can manage chapters.");
        }
    }
}
