using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the property <c>FirstAdminBootstrapService</c>'s doc comment rests on: the bootstrap is
/// reachable only from the <c>Vlms.Jobs</c> console host (shell access to the server), never from
/// <c>Vlms.Web</c>. A web-reachable bootstrap would let whoever registers first on a fresh
/// deployment claim Admin. Reads source off disk, same approach as <see cref="PwaAssetsTests"/>,
/// since Vlms.Tests doesn't reference Vlms.Web.
/// </summary>
public sealed class FirstAdminBootstrapWiringTests
{
    [Fact]
    public void VlmsWeb_NeverReferencesFirstAdminBootstrapService()
    {
        var webRoot = Path.Combine(RepoRoot(), "src", "Vlms.Web");

        var offenders = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => CodeLines(f).Any(line => line.Contains("FirstAdminBootstrapService", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0, $"Vlms.Web must not use FirstAdminBootstrapService: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void VlmsJobs_WiresTheBootstrapCommand()
    {
        var jobsProgram = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Jobs", "Program.cs"));

        Assert.Contains("AddScoped<FirstAdminBootstrapService>()", jobsProgram);
        Assert.Contains("\"bootstrap-admin\"", jobsProgram);
    }

    /// <summary>
    /// The file's lines minus <c>//</c>/<c>///</c> comment lines, so Program.cs's own comment
    /// explaining why the service is absent doesn't count as a use of it.
    /// </summary>
    private static IEnumerable<string> CodeLines(string path) =>
        File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

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
