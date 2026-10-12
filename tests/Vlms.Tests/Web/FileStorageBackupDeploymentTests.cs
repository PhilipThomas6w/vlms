using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the off-box file-storage backup (deploy/backup/files-backup.sh and its systemd units,
/// deploy/README.md §9). Reads the files off disk, same approach as <see cref="PwaAssetsTests"/>;
/// the script itself needs a live bucket to run end to end.
/// </summary>
public sealed class FileStorageBackupDeploymentTests
{
    [Fact]
    public void Script_SyncsToTheFilesPrefix_AndNeverDeletesOffBox()
    {
        var script = Read("deploy", "backup", "files-backup.sh");

        Assert.Contains("aws s3 sync \"$FILES_ROOT\" \"s3://${BACKUP_S3_BUCKET}/files/\" --endpoint-url \"$BACKUP_S3_ENDPOINT\"", script);
        Assert.DoesNotContain(CodeLines(script), line => line.Contains("--delete", StringComparison.Ordinal));
        Assert.DoesNotContain(CodeLines(script), line => line.Contains("s3 rm", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_RequiresFilesRoot_RatherThanDefaultingIt()
    {
        var script = Read("deploy", "backup", "files-backup.sh");

        Assert.Contains("for required_var in BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET FILES_ROOT; do", script);
        Assert.DoesNotContain(CodeLines(script), line => line.Contains("FILES_ROOT:-", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_HasARestoreMode()
    {
        var script = Read("deploy", "backup", "files-backup.sh");

        Assert.Contains("\"--restore\"", script);
        Assert.Contains("aws s3 sync \"s3://${BACKUP_S3_BUCKET}/files/\" \"$FILES_ROOT\"", script);
    }

    [Fact]
    public void Service_RunsUnprivileged_AndTimerRunsBetweenDbBackupAndSweep()
    {
        var service = Read("deploy", "backup", "vlms-files-backup.service");
        var timer = Read("deploy", "backup", "vlms-files-backup.timer");

        Assert.Contains("User=vlms", service);
        Assert.Contains("ExecStart=/opt/vlms/backup/files-backup.sh", service);
        Assert.Contains("EnvironmentFile=-/etc/vlms/backup.env", service);
        Assert.Contains("OnCalendar=*-*-* 02:30:00 UTC", timer);
        Assert.Contains("Unit=vlms-files-backup.service", timer);
    }

    [Fact]
    public void Readme_DocumentsInstallAndRestore_AndNoLongerListsFileBackupAsMissing()
    {
        var readme = Read("deploy", "README.md");

        Assert.Contains("sudo systemctl enable --now vlms-files-backup.timer", readme);
        Assert.Contains("## 9. Restoring file storage", readme);
        Assert.Contains("files-backup.sh --restore", readme);
        Assert.DoesNotContain("Do not assume file backups are covered", readme);
    }

    [Fact]
    public void DataProtectionKeys_AreNotShippedOffBox()
    {
        var script = Read("deploy", "backup", "files-backup.sh");

        Assert.DoesNotContain("/var/lib/vlms/keys", script);
    }

    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(line => !line.TrimStart().StartsWith('#'));

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
