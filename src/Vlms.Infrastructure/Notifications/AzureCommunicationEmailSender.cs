using Azure;
using Azure.Communication.Email;

namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// Real <see cref="IEmailSender"/> implementation backed by Azure Communication Services Email
/// (adr/0001-technology-stack.md). API shape verified against Microsoft Learn
/// (learn.microsoft.com/dotnet/api/azure.communication.email.emailclient.send) rather than guessed,
/// per CLAUDE.md's "verify, don't invent" rule: <see cref="EmailClient.SendAsync(WaitUntil, string, string, string, string, string, System.Threading.CancellationToken)"/>
/// returns an <see cref="EmailSendOperation"/> whose <c>Value.Status</c> is an
/// <see cref="EmailSendStatus"/> — only <see cref="EmailSendStatus.Succeeded"/> counts as success;
/// anything else (Failed/Canceled/NotStarted/Running, the operation not actually completing) throws
/// so <see cref="NotificationService"/>'s retry loop treats it the same as a thrown transport error.
///
/// Takes an already-constructed <see cref="EmailClient"/> (built from a connection string by the
/// caller — see <c>Vlms.Jobs/Program.cs</c>), the same "caller builds the SDK client, this class
/// just uses it" shape as <c>Storage/AzureBlobStorage.cs</c> takes a <c>BlobServiceClient</c>.
/// </summary>
public sealed class AzureCommunicationEmailSender : IEmailSender
{
    private readonly EmailClient _client;
    private readonly string _senderAddress;

    public AzureCommunicationEmailSender(EmailClient client, string senderAddress)
    {
        _client = client;
        _senderAddress = senderAddress;
    }

    public async Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
    {
        var operation = await _client.SendAsync(
            WaitUntil.Completed, _senderAddress, recipientEmail, subject, htmlContent: body, plainTextContent: body, cancellationToken: ct);

        if (operation.Value.Status != EmailSendStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Azure Communication Services Email send to {recipientEmail} did not succeed (status: {operation.Value.Status}).");
        }
    }
}
