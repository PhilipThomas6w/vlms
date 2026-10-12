using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the migrations-bundle deployment path (deploy/build-migrations-bundle.ps1,
/// deploy/migrate.sh, deploy/README.md §3/§4): the server applies a self-contained bundle as
/// vlms_owner and never needs the .NET SDK. Reads the files off disk, same approach as
/// <see cref="PwaAssetsTests"/>; the bundle itself is a linux-x64 binary that can't run here.
/// </summary>
public sealed class MigrationsBundleDeploymentTests
{
    [Fact]
    public void BuildScript_ProducesSelfContainedLinuxBundle_FromTheDesignTimeFactoryProject()
    {
        var script = Read("deploy", "build-migrations-bundle.ps1");

        Assert.Contains("migrations bundle", script);
        Assert.Contains("--self-contained", script);
        Assert.Contains("--target-runtime linux-x64", script);
        Assert.Contains("--startup-project (Join-Path $repo 'src' 'Vlms.Infrastructure')", script);
    }

    [Fact]
    public void MigrateScript_PassesConnectionAtRunTime_AsVlmsOwner_WithNoCommittedSecret()
    {
        var script = Read("deploy", "migrate.sh");

        Assert.Contains("--connection \"$VLMS_OWNER_CONNECTION\"", script);
        Assert.Contains("vlms_owner", script);
        Assert.Contains("/etc/vlms/migrate.env", script);
        Assert.DoesNotContain(script.Split('\n').Where(l => !l.TrimStart().StartsWith('#')),
            line => line.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MigrateScript_IsInstalledOutsideTheVlmsOwnedTree()
    {
        var script = Read("deploy", "migrate.sh");

        Assert.Contains("BUNDLE:-/opt/vlms-migrations/efbundle", script);
    }

    [Fact]
    public void DeployReadme_NoLongerNeedsTheSdkOnTheServer()
    {
        var readme = Read("deploy", "README.md");

        Assert.DoesNotContain("dotnet ef database update", readme);
        Assert.Contains("sudo /opt/vlms-migrations/migrate.sh", readme);
    }

    [Fact]
    public void BundleOutput_IsGitIgnored()
    {
        Assert.Contains("artifacts/", Read(".gitignore").Split('\n').Select(l => l.Trim()));
    }

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
