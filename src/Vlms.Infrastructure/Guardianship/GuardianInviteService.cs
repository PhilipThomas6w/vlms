using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vlms.Domain;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;

namespace Vlms.Infrastructure.Guardianship;

/// <summary>
/// The self-rolled replacement for the Microsoft Graph guardian-invitation blocker (STATE.md
/// "Identity" item 5; retires the former `## Blocked / needs decision` Entra-tenant item per
/// adr/0005-hosting-pivot-self-hosted-vps-postgresql.md's Consequences). Issues and accepts
/// <see cref="GuardianInvite"/> tokens — see that entity's doc comment for the full design
/// (when one is issued, what "signed"/"single-use"/"expiring" mean here).
///
/// **Called from <see cref="GuardianLinkService"/>, after a <see cref="StudentGuardianLink"/> is
/// created, through <see cref="InviteOrNotifyAsync"/>**, which issues an invite only when the
/// guardian has no account and no usable invite outstanding (one working invite per guardian), and
/// otherwise sends a note that the child was linked.
///
/// **Two ways to accept (functional.md FR-004, 22/10/2026):** <see cref="AcceptAsync"/> creates a
/// new account when the invited email has none; when it already belongs to an account (a Teacher
/// who is also a parent), the invitee signs in to that account and <see cref="AcceptForSignedInUserAsync"/>
/// links it. <see cref="GetAcceptModeAsync"/> tells the page which applies.
///
/// **Token shape — verified against current ASP.NET Core Data Protection guidance rather than
/// hand-rolling HMAC signing (CLAUDE.md "verify, don't invent"):** <see cref="IDataProtectionProvider.CreateProtector(string)"/>
/// with a fixed, versioned purpose string, then <c>.ToTimeLimitedDataProtector()</c>
/// (`Microsoft.AspNetCore.DataProtection.Extensions`) to <see cref="ITimeLimitedDataProtector.Protect(string, TimeSpan)"/>
/// this invite row's own <see cref="GuardianInvite.Id"/> (not the Student/ParentGuardian ids
/// themselves — the row they're scoped to is looked up from the id, so there is nothing else worth
/// putting in the token). This is Microsoft's own documented mechanism for exactly this shape of
/// problem ("Purpose strings in ASP.NET Core", "ASP.NET Core Data Protection — consumer APIs":
/// authenticated encryption, so tampering is detected as an exception, not silently accepted; the
/// built-in expiry is enforced by <see cref="ITimeLimitedDataProtector.Unprotect(string, out DateTimeOffset)"/>
/// itself) — not a bespoke HMAC scheme this codebase would then have to maintain and get right on
/// its own. The purpose string embeds a version marker (<see cref="Purpose"/>) so a future change to
/// what the token protects can invalidate old, in-flight tokens deliberately rather than by accident.
///
/// **Key ring:** persisted to a configured directory by <c>Security/VlmsDataProtection.cs</c>, so
/// invite tokens stay valid across a Vlms.Web restart. The <c>vlms</c> service user has no home
/// directory, so the framework's default user-profile location isn't available in production.
/// </summary>
public sealed class GuardianInviteService
{
    private const string Purpose = "Vlms.GuardianInvite.v1";

    /// <summary>
    /// No figure is named anywhere in the design docs for how long a guardian invite stays valid —
    /// a documented build-time default, the same "configurable, no number given" situation
    /// `ConsentExpiryJob.DefaultExpiryWarningWindowDays`/`NotificationService.DefaultSafeguardingCriticalMaxAttempts`
    /// already resolved for this codebase. 14 days: long enough that a guardian who doesn't check
    /// email daily isn't unfairly locked out, short enough that a stale, unused invite doesn't sit
    /// valid indefinitely.
    /// </summary>
    public static readonly TimeSpan DefaultInviteLifetime = TimeSpan.FromDays(14);

    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly INotificationService _notificationService;
    private readonly AppUserProvisioningService _provisioning;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _inviteLifetime;
    private readonly ChapterScopeOverride? _chapterScope;

    public GuardianInviteService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        INotificationService notificationService,
        AppUserProvisioningService provisioning,
        IDataProtectionProvider dataProtectionProvider,
        IConfiguration configuration,
        TimeSpan? inviteLifetime = null,
        ChapterScopeOverride? chapterScope = null)
    {
        _chapterScope = chapterScope;
        _db = db;
        _currentUser = currentUser;
        _notificationService = notificationService;
        _provisioning = provisioning;
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _configuration = configuration;
        _inviteLifetime = inviteLifetime ?? DefaultInviteLifetime;
    }

    /// <summary>
    /// Creates a <see cref="GuardianInvite"/> row scoped to <paramref name="studentId"/>/
    /// <paramref name="parentGuardianId"/> and emails the accept link. Admin only (adr/0008 answer 4) — same
    /// defense-in-depth role check as every other independently-callable service in this codebase
    /// (`GuardianLinkService`/`StudentRegistrationService`), even though the only current caller
    /// (<see cref="GuardianLinkService"/>) has already checked.
    /// </summary>
    public async Task<(GuardianInvite Invite, string Token)> IssueAsync(
        int studentId, int parentGuardianId, CancellationToken ct = default)
    {
        RequireAdmin();
        var createdByUserId = RequireResolvedUserId();

        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        var guardian = await _db.ParentGuardians.SingleAsync(g => g.Id == parentGuardianId, ct);

        var now = DateTimeOffset.UtcNow;
        var invite = new GuardianInvite
        {
            Id = await NextId(ct),
            StudentId = studentId,
            ParentGuardianId = parentGuardianId,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            ExpiresAt = now.Add(_inviteLifetime)
        };
        _db.GuardianInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        var token = _protector.Protect(invite.Id.ToString(CultureInfo.InvariantCulture), _inviteLifetime);

        await SendInviteEmailAsync(guardian, student, token, ct);

        return (invite, token);
    }

    /// <summary>
    /// Validates <paramref name="token"/> (signature, expiry, not-already-consumed), creates the
    /// guardian's Identity account via <see cref="AppUserProvisioningService"/>, links it to the
    /// invite's <see cref="ParentGuardian"/> (<see cref="ParentGuardian.AppUserId"/>), and marks the
    /// invite consumed — all inside one transaction (see <see cref="AppUserProvisioningService"/>'s
    /// doc comment for how it detects and joins this ambient transaction rather than opening its
    /// own). Does NOT sign the new account in — that requires <c>SignInManager</c>, which needs
    /// <c>HttpContext</c> to write the auth cookie and so deliberately stays in Vlms.Web's
    /// accept-invite page, not here (this project's existing Infrastructure/Web split — Infrastructure
    /// never depends on ASP.NET Core's hosting-only types).
    ///
    /// Refused when the invited email already belongs to an account (functional.md FR-004,
    /// 22/10/2026): that invitee signs in and accepts through <see cref="AcceptForSignedInUserAsync"/>
    /// instead. Before that decision this path tried to create a second account and failed with
    /// Identity's "Username is already taken".
    /// </summary>
    public async Task<AppUser> AcceptAsync(string token, string password, CancellationToken ct = default)
    {
        using var chapterScope = await EnterInviteChapterAsync(token, ct);
        var invite = await ValidateAndLoadInviteAsync(token, ct);
        var guardian = await LoadUnlinkedGuardianAsync(invite, ct);

        if (await _provisioning.FindAdultAccountIdByEmailAsync(guardian.ContactInfo) is not null)
        {
            throw new InvalidOperationException(SignInToAcceptMessage);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var (result, _, appUser) = await _provisioning.CreateAsync(
            guardian.ContactInfo, password, guardian.Name, ct, emailConfirmed: true);

        if (!result.Succeeded || appUser is null)
        {
            throw new InvalidOperationException(
                "Account creation failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        LinkGuardianAndConsumeInvite(guardian, invite, appUser.Id);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return appUser;
    }

    /// <summary>What the accept-invite page should offer for a valid token (functional.md FR-004).</summary>
    public enum AcceptMode
    {
        /// <summary>No account has the invited email: choose a password to create one.</summary>
        CreateAccount,

        /// <summary>An account already has the invited email and nobody is signed in: sign in to it to accept.</summary>
        SignInRequired,

        /// <summary>Signed in as the account that owns the invited email: accepting links it.</summary>
        ReadyToLink,

        /// <summary>Signed in as some other account: refused, without saying anything about the invited one.</summary>
        SignedInAsDifferentAccount
    }

    public const string AlreadyLinkedMessage =
        "This invite is no longer needed: the guardian it was sent to already has an account. Sign in to that account instead.";

    public const string SignInToAcceptMessage =
        "An account already exists for the email address this invite was sent to. Sign in to that account to accept the invite.";

    public const string DifferentAccountMessage =
        "You are signed in with a different account from the one this invite was sent to. Sign out, then sign in with the account the invite was sent to.";

    /// <summary>
    /// Validates <paramref name="token"/> (signature, expiry, single use, guardian not already
    /// linked to an account; each refusal throws exactly as <see cref="AcceptAsync"/> does) and says
    /// which acceptance path applies. Only a holder of a valid token can learn from this that the
    /// invited email already has an account, which functional.md FR-004 accepts. Writes nothing.
    /// </summary>
    public async Task<AcceptMode> GetAcceptModeAsync(string token, CancellationToken ct = default)
    {
        using var chapterScope = await EnterInviteChapterAsync(token, ct);
        var invite = await ValidateAndLoadInviteAsync(token, ct);
        var guardian = await LoadUnlinkedGuardianAsync(invite, ct);

        var existingAccountId = await _provisioning.FindAdultAccountIdByEmailAsync(guardian.ContactInfo);
        if (existingAccountId is null)
        {
            return AcceptMode.CreateAccount;
        }

        var callerId = _currentUser.UserId;
        if (callerId is null)
        {
            return AcceptMode.SignInRequired;
        }

        return callerId == existingAccountId ? AcceptMode.ReadyToLink : AcceptMode.SignedInAsDifferentAccount;
    }

    /// <summary>
    /// Accepts <paramref name="token"/> for the signed-in caller's existing account (functional.md
    /// FR-004, 22/10/2026): sets <see cref="ParentGuardian.AppUserId"/> to the caller and marks the
    /// invite consumed by them. Grants no role; an Admin still approves the guardian as
    /// <see cref="Role.Parent"/>, and that grant adds to the roles the account already holds.
    ///
    /// <b>Ownership is proved by signing in, never by the email alone:</b> the caller must be signed
    /// in (<see cref="ICurrentUserContext.UserId"/>, which comes only from the authentication cookie,
    /// so sign-in including any TOTP step has completed), and must BE the account Identity resolves
    /// from the invited email (same normalisation as sign-in, so case-insensitive). Any other
    /// signed-in account is refused with <see cref="DifferentAccountMessage"/>, which names nothing
    /// about the invited account. A used, expired or tampered token, or a guardian already linked to
    /// an account, is refused exactly as in <see cref="AcceptAsync"/>, so a guardian record is never
    /// re-linked to a different identity.
    /// </summary>
    public async Task<AppUser> AcceptForSignedInUserAsync(string token, CancellationToken ct = default)
    {
        using var chapterScope = await EnterInviteChapterAsync(token, ct);
        var invite = await ValidateAndLoadInviteAsync(token, ct);
        var guardian = await LoadUnlinkedGuardianAsync(invite, ct);

        var callerId = _currentUser.UserId
            ?? throw new UnauthorizedAccessException("Sign in to accept this invite.");

        var existingAccountId = await _provisioning.FindAdultAccountIdByEmailAsync(guardian.ContactInfo);
        if (existingAccountId != callerId)
        {
            throw new InvalidOperationException(DifferentAccountMessage);
        }

        var appUser = await _db.AppUsers.SingleAsync(u => u.Id == callerId, ct);

        LinkGuardianAndConsumeInvite(guardian, invite, appUser.Id);
        await _db.SaveChangesAsync(ct);

        return appUser;
    }

    /// <summary>
    /// Called by <see cref="GuardianLinkService"/> after it creates a <see cref="StudentGuardianLink"/>.
    /// Keeps to one working invite per guardian (STATE.md browser-walk finding, 22/10/2026: linking
    /// a second child used to send a second invite, which then failed as stale once the first was
    /// used):
    /// <list type="bullet">
    /// <item>Guardian already has an account: no invite; a note that the child was linked goes to
    /// that account's email.</item>
    /// <item>No account, and an unused invite that has not expired is outstanding: no new invite; a
    /// note tells the guardian the child was linked and the earlier invite still works. Accepting
    /// that one invite links the guardian record, which covers every child linked to it.</item>
    /// <item>No account and no usable invite (none yet, or the earlier one expired): a new invite via
    /// <see cref="IssueAsync"/>.</item>
    /// </list>
    /// Admin only, as <see cref="IssueAsync"/>.
    /// </summary>
    public async Task InviteOrNotifyAsync(int studentId, int parentGuardianId, CancellationToken ct = default)
    {
        RequireAdmin();

        var student = await _db.Students.SingleAsync(s => s.Id == studentId, ct);
        var guardian = await _db.ParentGuardians.SingleAsync(g => g.Id == parentGuardianId, ct);

        if (guardian.AppUserId is int appUserId)
        {
            var account = await _db.AppUsers.SingleAsync(u => u.Id == appUserId, ct);
            var recipient = string.IsNullOrWhiteSpace(account.Email) ? guardian.ContactInfo : account.Email;
            var dashboard = PublicBaseUrlOrNull() is { } baseUrl ? $"\n\n{baseUrl}/parent/dashboard" : "";
            await _notificationService.SendAsync(
                new NotificationRequest(
                    recipient, guardian.Name, $"{student.Name} has been linked to your account",
                    $"Hello {guardian.Name},\n\n{student.Name} has been linked to your Varangians LMS account. " +
                    "Once an Admin has approved you as a parent, you will see them on your dashboard." + dashboard,
                    NotificationPriority.Standard),
                ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var unused = await _db.GuardianInvites
            .Where(i => i.ParentGuardianId == parentGuardianId && i.ConsumedAt == null)
            .ToListAsync(ct);
        // Filtered in memory: SQLite can't order or compare DateTimeOffset in SQL, and the list is tiny.
        var usable = unused.Where(i => i.ExpiresAt > now).OrderByDescending(i => i.ExpiresAt).FirstOrDefault();

        if (usable is null)
        {
            _ = await IssueAsync(studentId, parentGuardianId, ct);
            return;
        }

        await _notificationService.SendAsync(
            new NotificationRequest(
                guardian.ContactInfo, guardian.Name, $"{student.Name} has been added to your invite",
                $"Hello {guardian.Name},\n\n{student.Name} has also been linked to you in the Varangians LMS. " +
                $"Use the invite email we sent you on {usable.CreatedAt.UtcDateTime:dd/MM/yyyy} to set up your account " +
                $"(it is valid until {usable.ExpiresAt.UtcDateTime:dd/MM/yyyy}). One account covers all of your children.",
                NotificationPriority.Standard),
            ct);
    }

    public const string ResendAlreadyLinkedMessage =
        "This guardian already has an account. Ask them to sign in, or to use \"Forgot password\" on the sign-in page; there is no invite to resend.";

    /// <summary>
    /// Resends the account-setup invite to a guardian who has no account yet (functional.md FR-018).
    /// Admin only (Teachers are not Admins; Philip, 22/10/2026). Refused when the guardian is already linked to an account. Every unused
    /// invite for the guardian is invalidated and a fresh one, with the normal lifetime, is issued
    /// to the guardian's contact email, so only the newest token works. The invite names the student
    /// of the guardian's most recent invite, or failing that of one of their links.
    ///
    /// **Invalidation needs no schema change:** the earlier invite rows get an <see cref="GuardianInvite.ExpiresAt"/>
    /// in the past. <c>ValidateAndLoadInviteAsync</c> already re-checks the row's own expiry after
    /// unprotecting the token, so the earlier (still cryptographically valid) token is refused.
    /// ConsumedAt is left alone because it records an acceptance. The invalidation and the new
    /// invite commit together: if the email cannot be sent, nothing changes.
    /// </summary>
    public async Task ResendAsync(int parentGuardianId, CancellationToken ct = default)
    {
        if (!_currentUser.HasRole(Role.Admin))
        {
            throw new UnauthorizedAccessException("Caller must hold the Admin role to resend a guardian invite.");
        }


        var guardian = await _db.ParentGuardians.SingleAsync(g => g.Id == parentGuardianId, ct);
        if (guardian.AppUserId is not null)
        {
            throw new InvalidOperationException(ResendAlreadyLinkedMessage);
        }

        var unused = await _db.GuardianInvites
            .Where(i => i.ParentGuardianId == parentGuardianId && i.ConsumedAt == null)
            .ToListAsync(ct);

        // Filtered/ordered in memory: SQLite can't order DateTimeOffset in SQL, and the list is tiny.
        var studentId = unused.OrderByDescending(i => i.CreatedAt).Select(i => (int?)i.StudentId).FirstOrDefault()
            ?? await _db.StudentGuardianLinks
                .Where(l => l.ParentGuardianId == parentGuardianId)
                .OrderBy(l => l.StudentId)
                .Select(l => (int?)l.StudentId)
                .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("This guardian is not linked to a student, so there is nothing to invite them to.");

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        foreach (var old in unused.Where(i => i.ExpiresAt > past))
        {
            _db.Entry(old).CurrentValues.SetValues(new GuardianInvite
            {
                Id = old.Id,
                StudentId = old.StudentId,
                ParentGuardianId = old.ParentGuardianId,
                CreatedByUserId = old.CreatedByUserId,
                CreatedAt = old.CreatedAt,
                ExpiresAt = past
            });
        }
        await _db.SaveChangesAsync(ct);

        _ = await IssueAsync(studentId, parentGuardianId, ct);

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }

    private async Task<ParentGuardian> LoadUnlinkedGuardianAsync(GuardianInvite invite, CancellationToken ct)
    {
        var guardian = await _db.ParentGuardians.SingleAsync(g => g.Id == invite.ParentGuardianId, ct);

        // A guardian record already linked to an identity is never re-linked (functional.md FR-004).
        // Reached by a second invite issued before InviteOrNotifyAsync kept to one per guardian, or
        // after the guardian accepted another invite.
        if (guardian.AppUserId is not null)
        {
            throw new InvalidOperationException(AlreadyLinkedMessage);
        }

        return guardian;
    }

    private void LinkGuardianAndConsumeInvite(ParentGuardian guardian, GuardianInvite invite, int appUserId)
    {
        _db.Entry(guardian).CurrentValues.SetValues(new ParentGuardian
        {
            Id = guardian.Id,
            Name = guardian.Name,
            ContactInfo = guardian.ContactInfo,
            IsPrimary = guardian.IsPrimary,
            AppUserId = appUserId
        });

        _db.Entry(invite).CurrentValues.SetValues(new GuardianInvite
        {
            Id = invite.Id,
            StudentId = invite.StudentId,
            ParentGuardianId = invite.ParentGuardianId,
            CreatedByUserId = invite.CreatedByUserId,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt,
            ConsumedAt = DateTimeOffset.UtcNow,
            ConsumedByAppUserId = appUserId
        });
    }

    private string? PublicBaseUrlOrNull() => _configuration["App:PublicBaseUrl"]?.TrimEnd('/');

    /// <summary>
    /// Unprotects <paramref name="token"/> (throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// if tampered or past Data Protection's own built-in expiry window), then re-checks expiry and
    /// single-use against the row itself — belt-and-braces, in case a future caller ever unprotects a
    /// token via a different path (the entity's own doc comment).
    /// </summary>
    /// <summary>
    /// Enters the invite's chapter for the rest of an accept call (docs/adr/0010-chapters.md). The
    /// person accepting is usually not signed in, so has no active chapter and the chapter query
    /// filters would hide the invite and its guardian record. Only after the token's Data Protection
    /// signature and expiry have been checked is the invite's chapter read, with one fixed scalar
    /// query that returns nothing but that chapter's id; then <see cref="ChapterScopeOverride"/>
    /// makes it the active chapter until the returned scope is disposed. The caller's roles are
    /// unchanged, so the sensitive-data filters still show an anonymous caller nothing. Every check
    /// in <see cref="ValidateAndLoadInviteAsync"/> still runs inside the scope.
    /// </summary>
    private async Task<IDisposable> EnterInviteChapterAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var inviteIdText = _protector.Unprotect(token, out var expiration);
        if (!int.TryParse(inviteIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inviteId))
        {
            throw new InvalidOperationException("Invite token could not be parsed.");
        }

        if (expiration < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("This invite link has expired.");
        }

        var chapterId = await _db.Database
            .SqlQuery<int>($"SELECT chapter_id AS \"Value\" FROM guardian_invites WHERE id = {inviteId}")
            .SingleOrDefaultAsync(ct);
        if (chapterId == 0)
        {
            throw new InvalidOperationException("Invite not found.");
        }

        return _chapterScope?.EnterForVerifiedInvite(chapterId) ?? NoScope.Instance;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private async Task<GuardianInvite> ValidateAndLoadInviteAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var inviteIdText = _protector.Unprotect(token, out var expiration);
        if (!int.TryParse(inviteIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inviteId))
        {
            throw new InvalidOperationException("Invite token could not be parsed.");
        }

        if (expiration < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("This invite link has expired.");
        }

        var invite = await _db.GuardianInvites.SingleOrDefaultAsync(i => i.Id == inviteId, ct)
            ?? throw new InvalidOperationException("Invite not found.");

        if (invite.ConsumedAt is not null)
        {
            throw new InvalidOperationException("This invite link has already been used.");
        }

        if (invite.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("This invite link has expired or been replaced by a newer one.");
        }

        return invite;
    }

    // Judgement call, recorded rather than silently assumed: data-design.md's ParentGuardian row
    // (`Id, Name, ContactInfo, IsPrimary, AppUserId`) never specifies ContactInfo's format — it could
    // be a phone number or an email address. There is no other field to draw an email from before an
    // account exists (AppUser.Email, used everywhere else in this codebase for notification
    // recipients — e.g. ConsentExpiryNotifier — only exists once an AppUser row does, which for a
    // guardian is exactly what this flow is creating). ContactInfo is therefore treated as an email
    // address here — both the invite's send-to address and the new account's Identity
    // UserName/Email. If a future increment splits ContactInfo into separate phone/email fields,
    // this is the place that needs revisiting.
    private async Task SendInviteEmailAsync(ParentGuardian guardian, Student student, string token, CancellationToken ct)
    {
        var baseUrl = _configuration["App:PublicBaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("App:PublicBaseUrl is not configured.");
        var acceptUrl = $"{baseUrl}/guardianship/accept-invite?token={Uri.EscapeDataString(token)}";

        // The invited email can already belong to an account at send time (an account created after
        // the guardian record, then a resend): say "sign in to accept", as the accept page does.
        var hasAccount = await _provisioning.FindAdultAccountIdByEmailAsync(guardian.ContactInfo) is not null;
        var action = hasAccount
            ? "An account already exists for this email address. To accept, sign in to that account and follow the link below "
            : "To set up your account and access your dashboard, click the link below ";
        var subject = hasAccount ? "Sign in to accept your Varangians LMS invite" : "Set up your Varangians LMS account";

        var body =
            $"Hello {guardian.Name},\n\n" +
            $"You've been added as a guardian for {student.Name} in the Varangians LMS. " +
            action +
            $"(valid for {_inviteLifetime.Days} days):\n\n{acceptUrl}\n\n" +
            "If you weren't expecting this, you can ignore this email.";

        // Standard priority, not SafeguardingCritical: a guardian-invite email is routine account
        // setup, not one of low-level-design.md's named safeguarding-critical categories.
        await _notificationService.SendAsync(
            new NotificationRequest(guardian.ContactInfo, guardian.Name, subject, body, NotificationPriority.Standard),
            ct);
    }

    public const string AccountExistsForNewGuardianMessage =
        "An account already exists for that email address. Use \"Link an existing guardian\" instead of registering a new guardian.";

    /// <summary>
    /// FR-019: refuses registering a new guardian whose email already belongs to an account (any
    /// account; matched the way Identity normalises emails, so case-insensitively). Called by
    /// <see cref="GuardianLinkService.RegisterGuardianAndLinkAsync"/> before anything is written.
    /// </summary>
    public async Task RequireNoAccountForNewGuardianAsync(string email)
    {
        if (await _provisioning.FindAdultAccountIdByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException(AccountExistsForNewGuardianMessage);
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
                "Caller must hold the Admin role to issue a guardian invite.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Same application-assigned-id pattern as GuardianLinkService.NextId — see its comment.
    private async Task<int> NextId(CancellationToken ct) =>
        await _db.NextIdAsync<GuardianInvite>(ct);
}
