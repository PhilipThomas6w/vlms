using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// Development-only <see cref="IEmailSender"/>: writes each email to a UTF-8 text file in a local
/// folder instead of sending it through Microsoft Graph. Philip's decision (22/10/2026): the
/// development machine holds no real <c>GraphMail:ClientSecret</c>, so the Development environment
/// never calls Graph. <c>Vlms.Web/Program.cs</c> and <c>Vlms.Jobs/Program.cs</c> register this in
/// place of <see cref="GraphEmailSender"/> only when the host environment is Development; every other
/// environment keeps <see cref="GraphEmailSender"/>.
///
/// Each file holds the recipient address, recipient name, subject and then the body, so a developer
/// can open the message and follow any link in it. File names are
/// <c>yyyyMMdd-HHmmss-fffffff-{guid}.txt</c> (UTC): they sort by time and cannot collide.
///
/// <b>Failures throw</b> (directory cannot be created, file cannot be written), per
/// <see cref="IEmailSender"/>'s contract.
/// </summary>
public sealed class PickupDirectoryEmailSender : IEmailSender
{
    public const string ConfigurationKey = "Mail:PickupDirectory";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly TimeProvider _time;

    public PickupDirectoryEmailSender(string directory, ILogger<PickupDirectoryEmailSender>? logger = null, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A pickup directory is required.", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
        _time = time ?? TimeProvider.System;
        logger?.LogInformation(
            "Development email: messages are written to {PickupDirectory} instead of being sent through Microsoft Graph.",
            _directory);
    }

    /// <summary>
    /// The configured <c>Mail:PickupDirectory</c>, or, when unset, <c>%USERPROFILE%\.vlms-local\mail</c>
    /// (Philip's development machine; the folder lives outside the repository).
    /// </summary>
    public static string ResolveDirectory(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vlms-local", "mail")
            : configured;
    }

    public async Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);

        var fileName = $"{_time.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.txt";
        var content = new StringBuilder()
            .Append("To: ").AppendLine(recipientEmail)
            .Append("Recipient name: ").AppendLine(recipientName)
            .Append("Subject: ").AppendLine(subject)
            .AppendLine()
            .Append(body)
            .ToString();

        await File.WriteAllTextAsync(Path.Combine(_directory, fileName), content, Utf8WithoutBom, ct);
    }
}
