using Microsoft.Extensions.Configuration;
using Vlms.Infrastructure.Notifications;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Direct tests of <see cref="PickupDirectoryEmailSender"/> (the Development-only sender that writes
/// emails to files) against a real temp directory, cleaned up on dispose. Also covers the
/// <c>Mail:PickupDirectory</c> resolution, including its default.
/// </summary>
public sealed class PickupDirectoryEmailSenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vlms-pickup-tests-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_WritesRecipientSubjectAndBody_IntoOneFile()
    {
        var directory = Path.Combine(_root, "mail");
        var sender = new PickupDirectoryEmailSender(directory);

        await sender.SendAsync("parent@example.com", "Ada Parent", "Reset your password", "Click here: https://example.test/reset?t=abc");

        var file = Assert.Single(Directory.GetFiles(directory));
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("To: parent@example.com", text);
        Assert.Contains("Recipient name: Ada Parent", text);
        Assert.Contains("Subject: Reset your password", text);
        Assert.EndsWith("Click here: https://example.test/reset?t=abc", text.TrimEnd());
        Assert.EndsWith(".txt", file);
    }

    [Fact]
    public async Task SendAsync_CreatesMissingDirectory()
    {
        var directory = Path.Combine(_root, "not", "yet", "there");
        Assert.False(Directory.Exists(directory));
        var sender = new PickupDirectoryEmailSender(directory);

        await sender.SendAsync("teacher@example.com", "Teacher", "Subject", "Body");

        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task SendAsync_TwoSends_ProduceTwoFiles()
    {
        var directory = Path.Combine(_root, "mail");
        var sender = new PickupDirectoryEmailSender(directory);

        await sender.SendAsync("a@example.com", "A", "First", "one");
        await sender.SendAsync("a@example.com", "A", "Second", "two");

        var texts = Directory.GetFiles(directory).Select(File.ReadAllText).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, t => t.Contains("Subject: First"));
        Assert.Contains(texts, t => t.Contains("Subject: Second"));
    }

    [Fact]
    public void ResolveDirectory_UsesConfiguredValue_WhenSet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:PickupDirectory"] = _root })
            .Build();

        Assert.Equal(_root, PickupDirectoryEmailSender.ResolveDirectory(configuration));
    }

    [Fact]
    public void ResolveDirectory_DefaultsUnderUserProfile_WhenUnset()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vlms-local", "mail"),
            PickupDirectoryEmailSender.ResolveDirectory(configuration));
    }
}
