using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// Real <see cref="IEmailSender"/> implementation backed by SMTP
/// (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md — replaces Azure Communication Services
/// Email/the now-removed <c>AzureCommunicationEmailSender.cs</c>, adr/0001-technology-stack.md's
/// superseded Email row). Sends via <a href="https://github.com/jstedfast/MailKit">MailKit</a>'s
/// <see cref="MailKit.Net.Smtp.SmtpClient"/>, not the obsolete <see cref="System.Net.Mail.SmtpClient"/>
/// — verified against Microsoft's own docs rather than assumed, per CLAUDE.md's "verify, don't
/// invent" rule: learn.microsoft.com/dotnet/api/system.net.mail.smtpclient's Remarks section reads
/// "We don't recommend that you use the SmtpClient class for new development because SmtpClient
/// doesn't support many modern protocols. Use MailKit or other libraries instead." MailKit's own
/// Connect/Authenticate/Send/Disconnect API shape (and the exact async overload signatures used
/// below) verified against mimekit.net/docs (T_MailKit_Net_Smtp_SmtpClient.htm and the individual
/// method pages) and against MailKit's own published usage example
/// (github.com/jstedfast/MailKit/blob/master/Documentation/Examples/SmtpExamples.cs,
/// "SendMessage": Connect → Authenticate → Send → Disconnect) — not guessed from memory.
///
/// Config-driven host/port/credentials/from-address/StartTLS — see the "Smtp" configuration
/// section in Vlms.Jobs/Program.cs and Vlms.Web/Program.cs, the same "caller reads config, this
/// class just takes primitives" shape <c>ConsentExpiryJob</c>'s <c>expiryWarningWindowDays</c>
/// constructor parameter already established (no live sending target has been picked yet — ADR-0005
/// names the org's existing Microsoft 365 mailbox (authenticated SMTP submission) or a free-tier
/// transactional provider as the two options, "final pick at implementation time" — this class
/// works with either, since both are plain authenticated-or-anonymous SMTP submission). Sending
/// credentials (<paramref name="username"/>/<paramref name="password"/>) are optional — a null/blank
/// username skips SMTP AUTH entirely (not every SMTP relay requires it); when supplied, both must be
/// present together.
///
/// A new <see cref="MailKit.Net.Smtp.SmtpClient"/>/connection is created per send (not pooled or
/// reused across calls) — the "fewest moving parts, simplest correct thing" choice also used by the
/// now-removed <c>AzureCommunicationEmailSender</c> (a fresh <c>EmailClient.SendAsync</c> call per
/// send) and consistent with <see cref="NotificationService"/>'s own retry loop, which already
/// treats each attempt as an independent operation.
///
/// Throw/success contract matches <c>AzureCommunicationEmailSender</c>'s exactly, since
/// <see cref="NotificationService"/>'s retry logic depends on it: any failure (connection refused,
/// TLS/auth failure, a rejected recipient, or any other SMTP protocol error) surfaces as an
/// exception MailKit itself throws — unlike the Azure Communication Services SDK (which returns an
/// <c>EmailSendStatus</c> that had to be checked explicitly and thrown on manually), MailKit's
/// <see cref="MailKit.Net.Smtp.SmtpClient"/> throws directly at the failing protocol step
/// (<c>ConnectAsync</c>/<c>AuthenticateAsync</c>/<c>SendAsync</c>), so this class does not need to
/// translate a success/failure status itself — it only needs to not swallow what MailKit already
/// throws, which it doesn't.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string _fromAddress;
    private readonly bool _useStartTls;

    public SmtpEmailSender(string host, int port, string? username, string? password, string fromAddress, bool useStartTls)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host must not be blank.", nameof(host));
        }

        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new ArgumentException("FromAddress must not be blank.", nameof(fromAddress));
        }

        if (string.IsNullOrWhiteSpace(username) != string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Username and Password must either both be supplied or both be left blank.");
        }

        _host = host;
        _port = port;
        _username = string.IsNullOrWhiteSpace(username) ? null : username;
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        _fromAddress = fromAddress;
        _useStartTls = useStartTls;
    }

    public async Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_fromAddress));
        message.To.Add(new MailboxAddress(recipientName, recipientEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // SecureSocketOptions.StartTls requires the server to support STARTTLS and fails loudly if
        // it doesn't; SecureSocketOptions.Auto negotiates opportunistically (used only when the
        // caller has explicitly not asked for StartTLS — e.g. a local/loopback relay in tests).
        var secureSocketOptions = _useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;

        using var client = new SmtpClient();
        await client.ConnectAsync(_host, _port, secureSocketOptions, ct);

        if (_username is not null && _password is not null)
        {
            await client.AuthenticateAsync(_username, _password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
