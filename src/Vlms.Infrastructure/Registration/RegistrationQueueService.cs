using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Registration;

/// <summary>How someone came to be waiting for a role in a chapter.</summary>
public enum QueueSource
{
    /// <summary>They registered and chose this chapter.</summary>
    SelfRegistration,

    /// <summary>A National Administrator sent an account that registered before chapters were asked for.</summary>
    PlacedByNationalAdministrator,

    /// <summary>They accepted one of this chapter's guardian invites and are not yet a Parent here.</summary>
    GuardianInvite
}

/// <summary>A teacher-to-be's latest DBS check in this chapter, for the queue.</summary>
public enum QueueDbsState
{
    None,
    Valid,
    Expired,
    Pending,
    Flagged
}

/// <summary>The latest DBS check (by expiry) of someone joining as a teacher; null fields when there is none.</summary>
public sealed record QueueDbsStatus(QueueDbsState State, DateOnly? ExpiryDate);

/// <summary>
/// One person waiting for a role in the Admin's chapter. <paramref name="CurrentRoles"/> are the
/// roles they already hold here (only ever non-empty for a guardian-invite acceptor, such as a
/// Teacher who is also a parent). <paramref name="CanDismiss"/> is true when they have an open
/// registration here to dismiss. <paramref name="Dbs"/> is set only for someone joining as a teacher.
/// </summary>
public sealed record QueueEntry(
    int UserId,
    string DisplayName,
    string Email,
    bool EmailConfirmed,
    JoiningAs? JoiningAs,
    DateTime? RegisteredAt,
    IReadOnlyList<QueueSource> Sources,
    IReadOnlyList<string> LinkedStudentNames,
    IReadOnlyList<Role> CurrentRoles,
    bool CanDismiss,
    QueueDbsStatus? Dbs);

/// <summary>Where a signed-in person stands when they hold no role anywhere (adr/0017 decision 3).</summary>
/// <param name="HoldsARole">True when they hold any role row (chapter or national, Student included): nothing to show.</param>
/// <param name="WaitingInChapters">The chapters whose queue they are on, by name.</param>
/// <param name="Dismissed">True when every registration they made was dismissed and they are on no queue.</param>
public sealed record MyRegistrationStatus(bool HoldsARole, IReadOnlyList<string> WaitingInChapters, bool Dismissed);

/// <summary>
/// The chapter's one registration queue (adr/0017, FR-021 as amended 10/11/2026): everyone waiting
/// for an Admin to give them a role in the chapter. It is the union of two existing lists, reused
/// rather than rebuilt: <see cref="NewRegistrationService.ListAsync"/> (people who registered for
/// the chapter, whatever they are joining as, and hold no role here) and
/// <see cref="RoleAssignmentService.ListAwaitingApprovalAsync"/> (people who accepted one of the
/// chapter's guardian invites and are not yet a Parent here). Someone on both is one entry.
///
/// <b>Authorization.</b> Every Admin method goes through those services, which require the Admin
/// role and act on the active chapter only; granting goes through
/// <see cref="RoleAssignmentService.GrantAsync(int, IReadOnlyCollection{Role}, CancellationToken)"/>
/// with all its guards (chapter roles only, no student sign-ins, all or none). The DBS status of a
/// teacher-to-be is read through the <c>DbsCheck</c> query filter (Admin or Safeguarding Officer
/// with MFA, this chapter), so each read is audited and nobody else sees one.
///
/// <b>Counts and the caller's own status</b> run on a context of their own: they are called from
/// the menu and the home page, whose components initialise alongside the page's own and would
/// otherwise share its DbContext (which cannot run two queries at once).
/// </summary>
public sealed class RegistrationQueueService
{
    private readonly VlmsDbContext _db;
    private readonly DbContextOptions<VlmsDbContext> _options;
    private readonly ICurrentUserContext _currentUser;
    private readonly NewRegistrationService _newRegistrations;
    private readonly RoleAssignmentService _roles;

    public RegistrationQueueService(
        VlmsDbContext db,
        DbContextOptions<VlmsDbContext> options,
        ICurrentUserContext currentUser,
        NewRegistrationService newRegistrations,
        RoleAssignmentService roles)
    {
        _db = db;
        _options = options;
        _currentUser = currentUser;
        _newRegistrations = newRegistrations;
        _roles = roles;
    }

    /// <summary>The active chapter's queue, longest waiting first. Admin-only.</summary>
    public async Task<IReadOnlyList<QueueEntry>> ListAsync(CancellationToken ct = default)
    {
        var registered = await _newRegistrations.ListAsync(ct);
        var invited = await _roles.ListAwaitingApprovalAsync(ct);

        var entries = new Dictionary<int, QueueEntry>();
        foreach (var person in registered)
        {
            entries[person.UserId] = new QueueEntry(
                person.UserId, person.DisplayName, person.Email, person.EmailConfirmed, person.JoiningAs, person.RequestedAt,
                [person.PlacedByNationalAdministrator ? QueueSource.PlacedByNationalAdministrator : QueueSource.SelfRegistration],
                [], [], CanDismiss: true, Dbs: null);
        }

        foreach (var person in invited)
        {
            var acceptedAt = person.AcceptedAt?.UtcDateTime;
            entries[person.UserId] = entries.TryGetValue(person.UserId, out var existing)
                ? existing with
                {
                    Sources = [.. existing.Sources, QueueSource.GuardianInvite],
                    LinkedStudentNames = person.LinkedStudentNames,
                    CurrentRoles = person.CurrentRoles
                }
                : new QueueEntry(
                    person.UserId, person.DisplayName, person.Email, person.EmailConfirmed, null, acceptedAt,
                    [QueueSource.GuardianInvite], person.LinkedStudentNames, person.CurrentRoles, CanDismiss: false, Dbs: null);
        }

        var teachers = entries.Values.Where(e => e.JoiningAs == JoiningAs.Teacher).Select(e => e.UserId).ToList();
        if (teachers.Count > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var checks = (await _db.DbsChecks.Where(d => teachers.Contains(d.TeacherUserId)).ToListAsync(ct))
                .GroupBy(d => d.TeacherUserId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.ExpiryDate).ThenByDescending(d => d.Id).First());
            foreach (var userId in teachers)
            {
                entries[userId] = entries[userId] with { Dbs = DbsStatusOf(checks.GetValueOrDefault(userId), today) };
            }
        }

        return entries.Values
            .OrderBy(e => e.RegisteredAt ?? DateTime.MaxValue)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static QueueDbsStatus DbsStatusOf(DbsCheck? check, DateOnly today) => check switch
    {
        null => new QueueDbsStatus(QueueDbsState.None, null),
        { ExpiryDate: var expiry } when expiry < today => new QueueDbsStatus(QueueDbsState.Expired, expiry),
        { Status: DbsCheckStatus.Clear } => new QueueDbsStatus(QueueDbsState.Valid, check.ExpiryDate),
        { Status: DbsCheckStatus.Flagged } => new QueueDbsStatus(QueueDbsState.Flagged, check.ExpiryDate),
        _ => new QueueDbsStatus(QueueDbsState.Pending, check.ExpiryDate)
    };

    /// <summary>
    /// Grants <paramref name="roles"/> (one or more chapter roles, all or none) to someone on the
    /// active chapter's queue, which takes them off it. Admin-only. Throws if they are not on the
    /// queue, or on any of <see cref="RoleAssignmentService"/>'s refusals.
    /// </summary>
    public async Task GrantAsync(int userId, IReadOnlyCollection<Role> roles, CancellationToken ct = default)
    {
        if ((await ListAsync(ct)).All(e => e.UserId != userId))
        {
            throw new InvalidOperationException("That person is not waiting for a role in this chapter.");
        }

        await _roles.GrantAsync(userId, roles, ct);
    }

    /// <summary>
    /// Takes someone who registered for the active chapter off its queue without a role. Deletes
    /// nothing. Admin-only (<see cref="NewRegistrationService.DismissAsync"/>).
    /// </summary>
    public Task DismissAsync(int userId, CancellationToken ct = default) => _newRegistrations.DismissAsync(userId, ct);

    /// <summary>
    /// What the Admin's Registrations page has waiting: people on the active chapter's queue plus
    /// children's applications waiting for the Admin's final approval. Zero for anyone who is not an
    /// Admin with two-factor authentication on (the menu and home page show it only to them).
    /// </summary>
    public async Task<int> CountWaitingAsync(CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Admin) || !_currentUser.HasMfaEnabled || _currentUser.ActiveChapterId is not int chapterId)
        {
            return 0;
        }

        await using var db = new VlmsDbContext(_options, _currentUser);
        var registered = await NewRegistrationService.OpenRequestsIn(db, chapterId).Select(r => r.UserId).ToListAsync(ct);
        var invited = await RoleAssignmentService.InviteAcceptorsAwaitingParentIn(db, chapterId).ToListAsync(ct);
        var applications = await db.StudentApplications.CountAsync(
            a => a.ChapterId == chapterId && a.Status == StudentApplicationStatus.AwaitingAdminApproval, ct);
        return registered.Union(invited).Count() + applications;
    }

    /// <summary>
    /// The signed-in caller's own registration status, for the home page of someone with no role
    /// (adr/0017 decision 3 and its flagged assumption). Reads only the caller's own rows.
    /// </summary>
    public async Task<MyRegistrationStatus> GetMyStatusAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is not int userId)
        {
            return new MyRegistrationStatus(true, [], false);
        }

        await using var db = new VlmsDbContext(_options, _currentUser);
        if (await db.UserRoles.AnyAsync(r => r.UserId == userId, ct))
        {
            return new MyRegistrationStatus(true, [], false);
        }

        // Holding no role, they are on a chapter's queue through an open registration there, or
        // through a guardian record an accepted invite linked to their account (which the guardian
        // filter shows them as their own row).
        var requests = await db.ChapterJoinRequests.Where(r => r.UserId == userId)
            .Select(r => new { r.ChapterId, r.DismissedAt })
            .ToListAsync(ct);
        var guardianChapters = await db.ParentGuardians.Where(g => g.AppUserId == userId).Select(g => g.ChapterId).ToListAsync(ct);
        var waitingIds = requests.Where(r => r.DismissedAt == null).Select(r => r.ChapterId).Concat(guardianChapters).Distinct().ToList();
        var names = await db.Chapters.Where(c => waitingIds.Contains(c.Id)).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);

        return new MyRegistrationStatus(false, names, Dismissed: waitingIds.Count == 0 && requests.Count > 0);
    }
}
