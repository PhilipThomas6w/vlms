using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Guardianship;

/// <summary>
/// Guardian-link creation (docs/design/low-level-design.md "Authorization model",
/// docs/design/data-design.md "Guardian link verification", docs/requirements/functional.md
/// FR-004, STATE.md): creates the <see cref="StudentGuardianLink"/> join row that a Parent's
/// resource-based access to a <see cref="Student"/>
/// (<see cref="Vlms.Infrastructure.Authorization.ParentStudentAccessHandler"/>) depends on.
///
/// Authorization: role-checked inside the service itself (defense in depth, same pattern as
/// <see cref="Vlms.Infrastructure.Curriculum.LessonProposalService"/>/
/// <see cref="Vlms.Infrastructure.Progress.CompletionService"/>) — restricted to Admin (Teachers too until 22/10/2026, adr/0008 answer 4).
/// This is a hard constraint (CLAUDE.md Project Law; functional.md FR-004; data-design.md
/// "Guardian link verification"): a <see cref="StudentGuardianLink"/> must never be created by
/// parent self-service. There is deliberately no code path anywhere in this codebase — service
/// method, page, or otherwise — that lets a Parent create their own link; both entry points below
/// (<see cref="CreateLinkAsync"/>/<see cref="RegisterGuardianAndLinkAsync"/>) role-check Admin/
/// Teacher before doing anything else, so a caller reaching this service by any path other than
/// the intended UI is still denied here, not just kept out by page-level policy gating.
///
/// Two entry points, matching data-design.md's "the Admin/Teacher enters the guardian's details"
/// wording:
/// - <see cref="RegisterGuardianAndLinkAsync"/> — the common case at a new student's registration:
///   the guardian has no <see cref="ParentGuardian"/> record yet, so this creates one and links it
///   in a single call.
/// - <see cref="CreateLinkAsync"/> — links an *existing* ParentGuardian (e.g. the same parent's
///   second child) without creating a duplicate ParentGuardian row.
///
/// Duplicate-link handling: <see cref="StudentGuardianLink"/>'s composite primary key
/// (StudentId, ParentGuardianId — see VlmsDbContext.OnModelCreating) already makes a second
/// identical link impossible at the database level; this service checks first and throws a clear
/// <see cref="InvalidOperationException"/> instead of surfacing a raw DbUpdateException/unique-
/// constraint violation to the caller.
///
/// Scope note (deliberate, per the STATE.md item's explicit wording): this service and its page
/// implement only the guardian-link creation flow FR-004 asks for — not Student or full
/// ParentGuardian CRUD/registration. In particular it does NOT open a Student's first
/// StudentRankProgress row (the precondition PromotionService's doc comment flags as expected to
/// land with "student registration"); that belongs to a not-yet-built student-registration/
/// enrolment flow, added as a new STATE.md Next item by this change.
///
/// **Guardian invite issuance (STATE.md "Identity" item 5, replacing the former Microsoft Graph
/// invitation blocker) — wired in here, not as a separate step the caller must remember:** both
/// entry points below call <see cref="IssueInviteIfUnregisteredAsync"/> immediately after creating
/// the <see cref="StudentGuardianLink"/>. The precise trigger condition is
/// <c>guardian.AppUserId is null</c> — NOT "new vs existing guardian" (data-design.md's own
/// "the Admin/Teacher enters the guardian's details" wording describes the common case, but the
/// correct condition is narrower): a guardian who already has an Identity account from an earlier
/// child needs no new invite when a second child is linked — <c>ParentGuardianLinkage</c>'s existing
/// join already grants them access to the new link via their one existing <c>AppUserId</c> the
/// moment it exists, so issuing another invite there would be pointless (and, if accepted, would
/// throw — <c>GuardianInviteService.AcceptAsync</c> rejects accepting an invite for a guardian who
/// already has an account). See <see cref="Vlms.Domain.GuardianInvite"/>'s doc comment for the full
/// design. **Amended 22/10/2026 (browser-walk finding):** the decision now lives in
/// <see cref="GuardianInviteService.InviteOrNotifyAsync"/>. A guardian with an account gets a note
/// that the child was linked instead of nothing, and a guardian with no account but a usable invite
/// outstanding gets a note pointing back to it instead of a second invite, so there is one working
/// invite per guardian.
///
/// **Atomicity (checker-FAIL round-trip fix on the STATE.md "Identity" item):** the link write
/// (this class's own <c>SaveChangesAsync</c>) and the invite write (a separate
/// <c>SaveChangesAsync</c> inside <see cref="GuardianInviteService.IssueAsync"/>) are two distinct
/// calls against the same <see cref="VlmsDbContext"/>. Both entry points below wrap the link write
/// and the <see cref="IssueInviteIfUnregisteredAsync"/> call in a transaction — the same non-
/// atomicity bug class <see cref="Vlms.Infrastructure.Registration.StudentRegistrationService"/>'s
/// own checker-FAIL round-trip already found and fixed (commit <c>d3a7e0d</c>) — so a failure
/// anywhere in invite issuance (the invite row's own write, or the subsequent invite-email send)
/// rolls back the link (and, on <see cref="RegisterGuardianAndLinkAsync"/>, the newly-created
/// <see cref="ParentGuardian"/> too) instead of leaving an orphaned link with no invite and no
/// onboarding path until someone notices and manually re-issues one.
///
/// **Composes with an already-open caller transaction, exactly like
/// <see cref="Vlms.Infrastructure.Identity.AppUserProvisioningService.CreateAsync"/>:** this class
/// is itself called from inside <see cref="Vlms.Infrastructure.Registration.StudentRegistrationService"/>'s
/// own <c>BeginTransactionAsync</c>/<c>CommitAsync</c> (both entry points there wrap their Student/
/// StudentRankProgress write plus the guardian-link call in one transaction) — a second, nested
/// <c>BeginTransactionAsync</c> on the same connection throws
/// ("The connection is already in a transaction and cannot participate in another transaction").
/// Both entry points below check <see cref="Microsoft.EntityFrameworkCore.DatabaseFacade.CurrentTransaction"/>
/// first: if one is already ambient (called from <c>StudentRegistrationService</c>), this class
/// joins it and leaves committing/rolling back to that caller; if not (called directly, e.g. from
/// <c>GuardianLinks.razor</c>), it opens and commits its own — the standard EF Core "compose
/// transactional units" pattern (see [Managing transactions](https://learn.microsoft.com/ef/core/saving/transactions),
/// "Using external DbTransactions"), not a bespoke transaction-passing parameter.
/// </summary>
public sealed class GuardianLinkService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly GuardianInviteService _guardianInvites;

    public GuardianLinkService(VlmsDbContext db, ICurrentUserContext currentUser, GuardianInviteService guardianInvites)
    {
        _db = db;
        _currentUser = currentUser;
        _guardianInvites = guardianInvites;
    }

    /// <summary>
    /// Links an existing <see cref="ParentGuardian"/> to a <see cref="Student"/>. Admin/Teacher
    /// only. Throws if either record doesn't exist, or if the link already exists.
    /// </summary>
    public async Task<StudentGuardianLink> CreateLinkAsync(
        int studentId, int parentGuardianId, CancellationToken ct = default)
    {
        RequireAdmin();
        var createdByUserId = RequireResolvedUserId();

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        var guardian = await _db.ParentGuardians.SingleAsync(g => g.Id == parentGuardianId, ct);

        await RequireNoExistingLinkAsync(studentId, parentGuardianId, ct);

        var link = new StudentGuardianLink
        {
            StudentId = studentId,
            ParentGuardianId = parentGuardianId,
            CreatedByUserId = createdByUserId
        };

        // Atomicity/ambient-transaction composition — see this class's doc comment.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            _db.StudentGuardianLinks.Add(link);
            await _db.SaveChangesAsync(ct);

            await IssueInviteIfUnregisteredAsync(studentId, guardian, ct);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(ct);
            }

            return link;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Creates a new <see cref="ParentGuardian"/> from the details entered by the Admin/Teacher,
    /// then links it to the Student — the common path at a new student's registration, where no
    /// ParentGuardian record exists yet. Admin only (Teachers lost this on 22/10/2026, adr/0008 answer 4).
    /// </summary>
    public async Task<(ParentGuardian Guardian, StudentGuardianLink Link)> RegisterGuardianAndLinkAsync(
        int studentId, string guardianName, string contactInfo, bool isPrimary, CancellationToken ct = default)
    {
        RequireAdmin();
        var createdByUserId = RequireResolvedUserId();
        ArgumentException.ThrowIfNullOrWhiteSpace(guardianName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactInfo);

        _ = await _db.Students.SingleAsync(s => s.Id == studentId, ct);

        // FR-019: an email that already has an account is linked as an existing guardian, never
        // registered again. Nothing has been written yet; Register student's transaction rolls back.
        await _guardianInvites.RequireNoAccountForNewGuardianAsync(contactInfo);

        var guardian = new ParentGuardian
        {
            Id = await NextId(_db.ParentGuardians, ct),
            Name = guardianName,
            ContactInfo = contactInfo,
            IsPrimary = isPrimary
        };
        _db.ParentGuardians.Add(guardian);

        var link = new StudentGuardianLink
        {
            StudentId = studentId,
            ParentGuardianId = guardian.Id,
            CreatedByUserId = createdByUserId
        };
        _db.StudentGuardianLinks.Add(link);

        // Atomicity/ambient-transaction composition — see this class's doc comment.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await _db.SaveChangesAsync(ct);

            // A brand-new ParentGuardian created right here always has AppUserId == null (it was
            // just constructed above with no AppUserId set), so this always issues an invite for
            // this entry point — unlike CreateLinkAsync, where the guardian may already be
            // registered.
            await IssueInviteIfUnregisteredAsync(studentId, guardian, ct);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(ct);
            }

            return (guardian, link);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// One working invite per guardian (STATE.md browser-walk finding, 22/10/2026): an invite only
    /// when the guardian has no account and no usable invite outstanding; otherwise a note that the
    /// child was linked. See <see cref="GuardianInviteService.InviteOrNotifyAsync"/>.
    /// </summary>
    private Task IssueInviteIfUnregisteredAsync(int studentId, ParentGuardian guardian, CancellationToken ct) =>
        _guardianInvites.InviteOrNotifyAsync(studentId, guardian.Id, ct);

    /// <summary>data-design.md's StudentGuardianLink composite key already prevents a duplicate row
    /// at the database level; this checks first so the caller sees a clear message rather than a
    /// raw unique-constraint violation.</summary>
    private async Task RequireNoExistingLinkAsync(int studentId, int parentGuardianId, CancellationToken ct)
    {
        var exists = await _db.StudentGuardianLinks.AnyAsync(
            l => l.StudentId == studentId && l.ParentGuardianId == parentGuardianId, ct);

        if (exists)
        {
            throw new InvalidOperationException(
                $"Student {studentId} is already linked to guardian {parentGuardianId}.");
        }
    }

    /// <summary>
    /// Admin only since 22/10/2026 (adr/0008 answer 4, functional.md FR-004): Teachers no longer
    /// register children or create guardian links. Parents apply instead
    /// (<see cref="Vlms.Infrastructure.Registration.StudentApplicationService"/>).
    /// </summary>
    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException(
                "Caller must hold the Admin role to create a guardian link.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private static async Task<int> NextId(DbSet<ParentGuardian> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
