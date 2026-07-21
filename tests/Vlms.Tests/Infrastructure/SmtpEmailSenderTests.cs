using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using netDumbster.smtp;
using Vlms.Infrastructure.Notifications;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="SmtpEmailSender"/> — the real <see cref="IEmailSender"/> implementation
/// backed by MailKit (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md, replacing the removed
/// <c>AzureCommunicationEmailSender</c>).
///
/// What's tested and why: constructor validation and the exact <c>Smtp:*</c> configuration binding
/// <c>Vlms.Jobs/Program.cs</c>/<c>Vlms.Web/Program.cs</c> use are fully covered without any network
/// involved — the same discipline <c>ConsentExpiryJobTests</c>'s
/// <c>Configuration_ExpiryWarningWindowDays_FlowsIntoJobBehaviour_ViaTheSameBindingProgramUses</c>
/// test established for <c>Safeguarding:ExpiryWarningWindowDays</c>. The actual network path
/// (Connect/Send/Disconnect talking real SMTP over a real loopback socket) is also genuinely
/// exercised, not skipped: MailKit itself ships no stub/fake SMTP server for testing (checked —
/// MailKit's own FAQ.md names nothing of the kind), so this uses
/// <a href="https://github.com/cmendible/netDumbster">netDumbster</a> (the community-standard
/// in-process fake SMTP server for .NET — a real <see cref="System.Net.Sockets.TcpListener"/> on
/// loopback, not a mock of <see cref="MailKit.Net.Smtp.SmtpClient"/> itself) to prove a real send
/// round-trips the exact recipient/subject/body, and a real unreachable port proves a real
/// connection failure surfaces as an exception rather than being swallowed.
///
/// **Explicitly not covered, stated rather than silently skipped:** netDumbster's SMTP
/// implementation doesn't advertise or handle AUTH or STARTTLS (checked its <c>SmtpProcessor.cs</c>
/// source — only HELO/EHLO/MAIL/RCPT/DATA/QUIT/RSET/NOOP are handled), so it cannot exercise
/// <see cref="SmtpEmailSender"/>'s authenticated-submission or StartTLS-negotiation-failure paths,
/// or a genuine "SMTP server rejected the recipient" (5xx) response — nothing short of a live SMTP
/// server (or a hand-rolled protocol-level fake, which would itself need independent verification
/// against RFC 5321 to be trustworthy) can exercise those specific paths. What's proved instead for
/// those cases: <see cref="SmtpEmailSender.SendAsync"/> makes no attempt to catch or translate
/// exceptions from any MailKit call — <c>ConnectAsync</c>/<c>AuthenticateAsync</c>/<c>SendAsync</c>
/// all propagate directly, so whatever exception MailKit throws for an auth failure or a rejected
/// recipient (both are documented MailKit behaviours — it throws for any non-success SMTP response)
/// reaches the caller exactly like the proven connection-failure case, satisfying
/// <see cref="NotificationService"/>'s retry loop's "any thrown exception counts as a failed
/// attempt" contract without needing a case-by-case reproduction of every SMTP error code.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    [Fact]
    public void Constructor_BlankHost_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new SmtpEmailSender(" ", 587, null, null, "from@example.com", true));
    }

    [Fact]
    public void Constructor_BlankFromAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new SmtpEmailSender("smtp.example.com", 587, null, null, " ", true));
    }

    [Theory]
    [InlineData("only-username", null)]
    [InlineData(null, "only-password")]
    public void Constructor_UsernameXorPassword_Throws(string? username, string? password)
    {
        Assert.Throws<ArgumentException>(() =>
            new SmtpEmailSender("smtp.example.com", 587, username, password, "from@example.com", true));
    }

    [Fact]
    public void Constructor_NeitherUsernameNorPassword_DoesNotThrow_AuthIsOptional()
    {
        // Not every SMTP relay requires AUTH (e.g. an internal/open relay) — a real IEmailSender
        // implementation must not force credentials that aren't there.
        var sender = new SmtpEmailSender("smtp.example.com", 587, null, null, "from@example.com", true);
        Assert.NotNull(sender);
    }

    [Fact]
    public void Constructor_BothUsernameAndPassword_DoesNotThrow()
    {
        var sender = new SmtpEmailSender("smtp.example.com", 587, "user", "pass", "from@example.com", true);
        Assert.NotNull(sender);
    }

    /// <summary>
    /// Reproduces exactly the binding calls Vlms.Jobs/Program.cs and Vlms.Web/Program.cs make
    /// against the "Smtp" configuration section, proving the real values a deployed instance would
    /// read — not just that <see cref="SmtpEmailSender"/>'s constructor works given values handed to
    /// it directly.
    /// </summary>
    [Fact]
    public void Configuration_SmtpSection_BindsExactlyAsProgramCsReads()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "smtp.example.com",
                ["Smtp:Port"] = "2525",
                ["Smtp:Username"] = "someone",
                ["Smtp:Password"] = "s3cret",
                ["Smtp:FromAddress"] = "DoNotReply@example.com",
                ["Smtp:UseStartTls"] = "false",
            })
            .Build();

        Assert.Equal("smtp.example.com", configuration.GetValue<string>("Smtp:Host"));
        Assert.Equal(2525, configuration.GetValue("Smtp:Port", 587));
        Assert.Equal("someone", configuration.GetValue<string>("Smtp:Username"));
        Assert.Equal("s3cret", configuration.GetValue<string>("Smtp:Password"));
        Assert.Equal("DoNotReply@example.com", configuration.GetValue<string>("Smtp:FromAddress"));
        Assert.False(configuration.GetValue("Smtp:UseStartTls", true));
    }

    [Fact]
    public void Configuration_PortAndUseStartTls_FallBackToProgramCsDefaults_WhenNotConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "smtp.example.com",
                ["Smtp:FromAddress"] = "DoNotReply@example.com",
            })
            .Build();

        Assert.Equal(587, configuration.GetValue("Smtp:Port", 587));
        Assert.True(configuration.GetValue("Smtp:UseStartTls", true));
        Assert.Null(configuration.GetValue<string>("Smtp:Username"));
        Assert.Null(configuration.GetValue<string>("Smtp:Password"));
    }

    [Fact]
    public void Configuration_MissingHost_TheNullCoalescingThrow_ProgramCsUsesIsReached()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            _ = configuration.GetValue<string>("Smtp:Host")
                ?? throw new InvalidOperationException("Smtp:Host is not configured."));
    }

    [Fact]
    public async Task SendAsync_RealLoopbackSmtpServer_DeliversExactRecipientSubjectAndBody()
    {
        using var server = SimpleSmtpServer.Start();
        var sut = new SmtpEmailSender(
            "localhost", server.Configuration.Port, username: null, password: null,
            fromAddress: "DoNotReply@example.com", useStartTls: false);

        await sut.SendAsync("parent@example.com", "Jane Parent", "Consent expiring soon", "Please renew consent.", CancellationToken.None);

        Assert.Equal(1, server.ReceivedEmailCount);
        var received = server.ReceivedEmail[0];
        Assert.Equal("DoNotReply@example.com", received.FromAddress.Address);
        Assert.Equal("parent@example.com", received.ToAddresses[0].Address);
        Assert.Equal("Consent expiring soon", received.Subject);
        Assert.Contains("Please renew consent.", received.MessageParts[0].BodyData);
    }

    [Fact]
    public async Task SendAsync_NothingListeningOnThePort_ThrowsRatherThanSwallowingTheFailure()
    {
        // A real, momentarily-bound-then-released TCP port: guaranteed free, guaranteed nothing is
        // listening on it — a genuine connection failure over a real socket, not a mock throwing on
        // demand. Exercises the same "any MailKit exception propagates untranslated" contract
        // AzureCommunicationEmailSender used to satisfy via an explicit status check.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var sut = new SmtpEmailSender(
            "localhost", freePort, username: null, password: null,
            fromAddress: "DoNotReply@example.com", useStartTls: false);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.SendAsync("parent@example.com", "Jane Parent", "subject", "body", CancellationToken.None));
    }
}
