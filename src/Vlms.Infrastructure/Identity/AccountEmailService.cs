using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Vlms.Domain;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// Email-confirmation and password-reset flows (STATE.md "Password reset + Identity hardening").
/// Tokens come from <see cref="UserManager{TUser}"/>'s default token provider (single-use, since
/// confirming or resetting rotates the security stamp; lifetime in <see cref="VlmsIdentityPolicy"/>)
/// and are emailed as absolute links built from <c>App:PublicBaseUrl</c>, the same way
/// <c>GuardianInviteService</c> builds its invite link.
///
/// <b>No account enumeration:</b> the request methods behave identically whether or not the email
/// matches an account, and the pages show the same message either way. A reset link is only ever
/// sent to a confirmed address, so an unconfirmed (possibly squatted) account can't be taken over
/// through it.
/// </summary>
public sealed class AccountEmailService
{
    private readonly UserManager<VlmsIdentityUser> _userManager;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;
    private readonly VlmsDbContext _db;

    public AccountEmailService(
        UserManager<VlmsIdentityUser> userManager,
        INotificationService notificationService,
        IConfiguration configuration,
        VlmsDbContext db)
    {
        _userManager = userManager;
        _notificationService = notificationService;
        _configuration = configuration;
        _db = db;
    }

    /// <summary>Sends an email-confirmation link to a newly registered, unconfirmed account.</summary>
    public async Task SendConfirmationAsync(VlmsIdentityUser user, CancellationToken ct = default)
    {
        var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = BuildLink("Account/ConfirmEmail", ("userId", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("code", code));

        await SendAsync(user, "Confirm your Varangians LMS email address",
            "Please confirm your email address to finish setting up your account " +
            $"(link valid for {HoursText()}):\n\n{link}\n\n" +
            "If you didn't register, you can ignore this email.", ct);
    }

    /// <summary>Re-sends the confirmation link if <paramref name="email"/> belongs to an unconfirmed account; otherwise does nothing.</summary>
    public async Task ResendConfirmationAsync(string email, CancellationToken ct = default)
    {
        var user = await FindAsync(email);
        if (user is not null && !await _userManager.IsEmailConfirmedAsync(user))
        {
            await SendConfirmationAsync(user, ct);
        }
    }

    /// <summary>Confirms the account's email. Returns false for an unknown user or an invalid/expired code.</summary>
    public async Task<bool> ConfirmEmailAsync(int userId, string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return user is not null && (await _userManager.ConfirmEmailAsync(user, code)).Succeeded;
    }

    /// <summary>
    /// Sends a password-reset link if <paramref name="email"/> belongs to a confirmed account;
    /// otherwise does nothing. A staff member who has left and holds no other role
    /// (<see cref="LeftStaffPolicy.IsLockedOut"/>, FR-017) gets no link: a new password would not let
    /// them sign in, and the page says the same thing whether or not a link was sent, so the
    /// account's state is not revealed. One who is also a parent still gets one.
    /// </summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        var user = await FindAsync(email);
        if (user is null || !await _userManager.IsEmailConfirmedAsync(user)
            || await LeftStaffPolicy.IsLockedOutAsync(_db, user.Id, ct))
        {
            return;
        }

        var code = await _userManager.GeneratePasswordResetTokenAsync(user);
        var link = BuildLink("Account/ResetPassword", ("code", code));

        await SendAsync(user, "Reset your Varangians LMS password",
            $"Someone asked to reset the password for this account. To choose a new password, use this link (valid for {HoursText()}):\n\n{link}\n\n" +
            "If you didn't ask for this, ignore this email — your password hasn't changed.", ct);
    }

    /// <summary>
    /// Sets a new password using a reset code. An unknown email reports success (no enumeration);
    /// an invalid/expired code or a password that fails validation reports the Identity errors.
    /// </summary>
    public async Task<IdentityResult> ResetPasswordAsync(string email, string code, string newPassword)
    {
        var user = await FindAsync(email);
        if (user is null)
        {
            return IdentityResult.Success;
        }

        return await _userManager.ResetPasswordAsync(user, code, newPassword);
    }

    private async Task<VlmsIdentityUser?> FindAsync(string email) =>
        string.IsNullOrWhiteSpace(email) ? null : await _userManager.FindByEmailAsync(email.Trim());

    private Task SendAsync(VlmsIdentityUser user, string subject, string body, CancellationToken ct) =>
        _notificationService.SendAsync(
            new NotificationRequest(user.Email!, user.Email!, subject, body, NotificationPriority.Standard), ct);

    private string BuildLink(string path, params (string Name, string Value)[] query)
    {
        var baseUrl = _configuration["App:PublicBaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("App:PublicBaseUrl is not configured.");
        return $"{baseUrl}/{path}?" + string.Join("&", query.Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value)}"));
    }

    private static string HoursText() =>
        VlmsIdentityPolicy.EmailTokenLifespan.TotalHours == 1 ? "1 hour" : $"{VlmsIdentityPolicy.EmailTokenLifespan.TotalHours:0} hours";
}
