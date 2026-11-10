using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// New registrations are visible and announced (functional.md FR-021 as amended 09/11/2026).
/// <list type="bullet">
/// <item><see cref="RegisterAsync"/>: the Register page. Creates the account and its
/// <see cref="ChapterJoinRequest"/> for the open chapter the person chose, in one transaction, then
/// emails that chapter's active Admins. Grants no role (deny-by-default stays).</item>
/// <item><see cref="ListAsync"/> and <see cref="DismissAsync"/>: the self-registration half of the
/// Admin's registration queue for their active chapter (adr/0017; shown on the Registrations page by
/// <c>Registration.RegistrationQueueService</c>): open requests whose person holds no role there yet.</item>
/// <item><see cref="ListUnplacedAsync"/> and <see cref="PlaceAsync"/>: a National Administrator
/// (with two-factor authentication on, as for every chapter-management action) sees adult accounts
/// that hold no role anywhere and are on no chapter's list, and sends one to a chapter's list.</item>
/// </list>
/// A chapter still sees only the people who chose it (or were placed in it), never every
/// registration on the platform. Emails carry the person's name and what they are joining as only,
/// are sent after the save and never throw: a failure is logged and the registration stands.
/// </summary>
public sealed class NewRegistrationService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly AppUserProvisioningService _provisioning;
    private readonly INotificationService _notifications;
    private readonly ILogger<NewRegistrationService> _logger;
    private readonly string? _publicBaseUrl;

    public NewRegistrationService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        AppUserProvisioningService provisioning,
        INotificationService notifications,
        ILogger<NewRegistrationService> logger,
        string? publicBaseUrl)
    {
        _db = db;
        _currentUser = currentUser;
        _provisioning = provisioning;
        _notifications = notifications;
        _logger = logger;
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');
    }

    /// <summary>One person on a chapter's "New registrations" list.</summary>
    public sealed record NewRegistration(
        int UserId, string DisplayName, string Email, JoiningAs? JoiningAs, DateTime RequestedAt, bool EmailConfirmed, bool PlacedByNationalAdministrator);

    /// <summary>An adult account with no role anywhere and on no chapter's list.</summary>
    public sealed record UnplacedAccount(int UserId, string DisplayName, string Email, bool EmailConfirmed);

    /// <summary>
    /// Creates the account and its request for <paramref name="chapterId"/>, which must be an open
    /// chapter, then emails that chapter's Admins. Identity failures (weak password, email in use)
    /// come back in the result unchanged, with nothing written.
    /// </summary>
    public async Task<(IdentityResult Result, VlmsIdentityUser? IdentityUser)> RegisterAsync(
        string email, string password, string displayName, int chapterId, JoiningAs joiningAs, CancellationToken ct = default)
    {
        if (!await _db.Chapters.AnyAsync(c => c.Id == chapterId && c.IsActive, ct))
        {
            return (IdentityResult.Failed(new IdentityError { Code = "Chapter", Description = "Choose the chapter you are joining." }), null);
        }

        VlmsIdentityUser identityUser;
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            var (result, created, _) = await _provisioning.CreateAsync(email, password, displayName, ct);
            if (!result.Succeeded || created is null)
            {
                return (result, null);
            }

            _db.ChapterJoinRequests.Add(new ChapterJoinRequest
            {
                UserId = created.Id,
                ChapterId = chapterId,
                JoiningAs = joiningAs,
                RequestedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            identityUser = created;
        }

        await NotifyAdminsAsync(chapterId, displayName, joiningAs, ct);
        return (IdentityResult.Success, identityUser);
    }

    /// <summary>
    /// The active chapter's open requests whose person holds no role there, oldest first. Admin-only.
    /// </summary>
    public async Task<IReadOnlyList<NewRegistration>> ListAsync(CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();

        var rows = await OpenRequestsIn(_db, chapterId)
            .Join(_db.AppUsers, r => r.UserId, u => u.Id, (r, u) => new { r, u })
            .Select(x => new
            {
                x.u.Id, x.u.DisplayName, x.u.Email, x.r.JoiningAs, x.r.RequestedAt, x.r.PlacedByUserId,
                Confirmed = _db.Users.Any(i => i.Id == x.u.Id && i.EmailConfirmed)
            })
            .ToListAsync(ct);

        // One line per person: their earliest open request here.
        return rows
            .GroupBy(x => x.Id)
            .Select(g => g.OrderBy(x => x.RequestedAt).First())
            .OrderBy(x => x.RequestedAt)
            .Select(x => new NewRegistration(x.Id, x.DisplayName, x.Email, x.JoiningAs, x.RequestedAt, x.Confirmed, x.PlacedByUserId != null))
            .ToList();
    }

    /// <summary>
    /// The open requests in <paramref name="chapterId"/> whose person (an adult account with an
    /// email) holds no role there: the self-registration half of the chapter's registration queue
    /// (adr/0017). Shared with <c>Registration.RegistrationQueueService</c>'s count so the list and
    /// the count can never disagree. The caller checks the role.
    /// </summary>
    internal static IQueryable<ChapterJoinRequest> OpenRequestsIn(VlmsDbContext db, int chapterId) =>
        db.ChapterJoinRequests.Where(r => r.ChapterId == chapterId && r.DismissedAt == null
            && !db.UserRoles.Any(u => u.UserId == r.UserId && u.ChapterId == chapterId)
            && db.AppUsers.Any(u => u.Id == r.UserId && u.Email != ""));

    /// <summary>
    /// Takes <paramref name="userId"/> off the active chapter's list without granting a role. Deletes
    /// nothing. Admin-only. Throws if they are not on the list.
    /// </summary>
    public async Task DismissAsync(int userId, CancellationToken ct = default)
    {
        var chapterId = RequireAdmin();
        var open = await _db.ChapterJoinRequests
            .Where(r => r.UserId == userId && r.ChapterId == chapterId && r.DismissedAt == null)
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            throw new InvalidOperationException("That person is not on this chapter's new registrations list.");
        }

        var now = DateTime.UtcNow;
        foreach (var request in open)
        {
            request.DismissedAt = now;
            request.DismissedByUserId = _currentUser.UserId;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adult accounts (not student sign-ins) holding no role in any chapter or nationally and with no
    /// open request in any chapter, newest first. National Administrator only.
    /// </summary>
    public async Task<IReadOnlyList<UnplacedAccount>> ListUnplacedAsync(CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        return await _db.AppUsers
            .Where(u => u.Email != ""
                && !_db.UserRoles.Any(r => r.UserId == u.Id)
                && !_db.ChapterJoinRequests.Any(r => r.UserId == u.Id && r.DismissedAt == null))
            .OrderByDescending(u => u.Id)
            .Select(u => new UnplacedAccount(u.Id, u.DisplayName, u.Email, _db.Users.Any(i => i.Id == u.Id && i.EmailConfirmed)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Puts an unplaced account on <paramref name="chapterId"/>'s list and emails that chapter's
    /// Admins. National Administrator only. Throws if the chapter is closed or unknown, or the
    /// account is not one <see cref="ListUnplacedAsync"/> would show.
    /// </summary>
    public async Task PlaceAsync(int userId, int chapterId, CancellationToken ct = default)
    {
        RequireNationalAdministrator();
        if (!await _db.Chapters.AnyAsync(c => c.Id == chapterId && c.IsActive, ct))
        {
            throw new InvalidOperationException("Choose an open chapter.");
        }

        var user = await _db.AppUsers
            .Where(u => u.Id == userId && u.Email != ""
                && !_db.UserRoles.Any(r => r.UserId == u.Id)
                && !_db.ChapterJoinRequests.Any(r => r.UserId == u.Id && r.DismissedAt == null))
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("That account already holds a role or is already on a chapter's list.");

        _db.ChapterJoinRequests.Add(new ChapterJoinRequest
        {
            UserId = user.Id,
            ChapterId = chapterId,
            JoiningAs = null,
            RequestedAt = DateTime.UtcNow,
            PlacedByUserId = _currentUser.UserId
        });
        await _db.SaveChangesAsync(ct);

        await NotifyAdminsAsync(chapterId, user.DisplayName, null, ct);
    }

    private async Task NotifyAdminsAsync(int chapterId, string displayName, JoiningAs? joiningAs, CancellationToken ct)
    {
        try
        {
            var admins = await ChapterMembership.StaffIn(_db, chapterId, Role.Admin)
                .Where(u => u.Email != "")
                .OrderBy(u => u.Id)
                .Select(u => new { u.Email, u.DisplayName })
                .ToListAsync(ct);

            var joining = joiningAs is null ? "" : $" They said they are joining as: {((JoiningAs?)joiningAs).DisplayName().ToLowerInvariant()}.";
            var subject = $"VLMS: new registration ({displayName})";
            var body = $"{displayName} has registered and is waiting for a role in your chapter.{joining} "
                + "No role has been given; they can do nothing until an Admin grants one."
                + (_publicBaseUrl is null ? "" : $"\n\nGive them a role here: {_publicBaseUrl}/registration/approve")
                + "\n\nVarangians";

            foreach (var admin in admins)
            {
                await _notifications.SendAsync(
                    new NotificationRequest(admin.Email, admin.DisplayName, subject, body, NotificationPriority.Standard), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "New-registration email to chapter {ChapterId}'s Admins failed; the registration stands.", chapterId);
        }
    }

    private int RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to view or change new registrations.");
        }

        return chapterId;
    }

    private void RequireNationalAdministrator()
    {
        if (!_currentUser.HasRole(Role.NationalAdministrator) || !_currentUser.HasMfaEnabled)
        {
            throw new UnauthorizedAccessException(
                "Only a National Administrator with two-factor authentication turned on can place registrations.");
        }
    }
}
