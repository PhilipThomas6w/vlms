using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the alert route's deployment files (deploy/systemd/vlms-alert.*, docs/operations/monitoring.md §3):
/// the command, the timer, the journal group, and an <c>OnFailure=</c> hook on every <c>vlms-*</c> service unit.
/// The units cannot be run without a Linux host, so these read the files off disk.
/// </summary>
public sealed class AlertDeploymentTests
{
    [Fact]
    public void AlertService_RunsTheDigestCommand_UnprivilegedWithJournalAccessAndAStateDirectory()
    {
        var service = Read("deploy", "systemd", "vlms-alert.service");

        Assert.Contains("ExecStart=/usr/bin/dotnet /opt/vlms/jobs/Vlms.Jobs.dll alert-digest", service);
        Assert.Contains("User=vlms", service);
        Assert.Contains("SupplementaryGroups=systemd-journal", service);
        Assert.Contains("StateDirectory=vlms-alert", service);
        Assert.Contains("EnvironmentFile=-/etc/vlms/jobs.env", service);
        Assert.Contains("Type=oneshot", service);
        Assert.DoesNotContain(CodeLines(service), line => line == "[Install]");
        Assert.DoesNotContain(CodeLines(service), line => line.StartsWith("OnFailure=", StringComparison.Ordinal));
    }

    [Fact]
    public void AlertTimer_RunsTheAlertServiceDaily_InTheMorning()
    {
        var timer = Read("deploy", "systemd", "vlms-alert.timer");

        Assert.Contains("OnCalendar=*-*-* 07:30:00 UTC", timer);
        Assert.Contains("Unit=vlms-alert.service", timer);
        Assert.Contains("WantedBy=timers.target", timer);
    }

    [Theory]
    [InlineData("deploy", "systemd", "vlms-web.service")]
    [InlineData("deploy", "systemd", "vlms-jobs.service")]
    [InlineData("deploy", "systemd", "vlms-retention.service")]
    [InlineData("deploy", "backup", "vlms-backup.service")]
    [InlineData("deploy", "backup", "vlms-files-backup.service")]
    public void EveryServiceUnit_StartsTheAlertOnFailure(params string[] path)
    {
        Assert.Contains(CodeLines(Read(path)), line => line == "OnFailure=vlms-alert.service");
    }

    [Fact]
    public void EveryVlmsServiceUnit_IsCoveredByAnOnFailureHook()
    {
        var units = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "deploy"), "vlms-*.service", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "vlms-alert.service");

        foreach (var unit in units)
        {
            Assert.Contains(CodeLines(File.ReadAllText(unit)), line => line == "OnFailure=vlms-alert.service");
        }
    }

    [Fact]
    public void Readme_DocumentsInstallingTheAlertTimer()
    {
        var readme = Read("deploy", "README.md");

        Assert.Contains("sudo systemctl enable --now vlms-alert.timer", readme);
        Assert.Contains("deploy/systemd/vlms-alert.service", readme);
    }

    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !line.TrimStart().StartsWith('#'));

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
