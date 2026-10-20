namespace Vlms.Infrastructure.Storage;

/// <summary>
/// Resolves <see cref="LocalDiskBlobStorage"/>'s root directory from <see cref="RootPathKey"/> and,
/// in Production, refuses a root that a release would destroy. A release replaces the published
/// output with <c>rsync --delete</c> into <c>/opt/vlms/web</c> (docs/operations/release.md §3), so a
/// root inside the app directory — including the <see cref="LocalDiskBlobStorage.DefaultRootPath"/>
/// fallback, which is relative to the working directory — would delete every certificate PDF on the
/// next deploy (docs/operations/configuration.md). Production therefore needs an explicit, absolute
/// path outside both the content root and the app's base directory. The path itself is an operator
/// choice set in <c>/etc/vlms/web.env</c>, and must equal <c>FILES_ROOT</c> in the backup config.
/// Called once at startup (Program.cs), so a bad value stops the process rather than surfacing on
/// the first certificate write.
/// </summary>
public static class BlobStorageRoot
{
    public const string RootPathKey = "Storage:LocalRootPath";

    /// <param name="configuredRootPath">The <see cref="RootPathKey"/> value; may be blank.</param>
    /// <param name="requireExternalRoot">True in Production.</param>
    /// <param name="appDirectories">Directories the root must not be inside (content root, base directory).</param>
    /// <returns>The full root path.</returns>
    public static string Resolve(string? configuredRootPath, bool requireExternalRoot, params string[] appDirectories)
    {
        if (!requireExternalRoot)
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRootPath)
                ? LocalDiskBlobStorage.DefaultRootPath
                : configuredRootPath);
        }

        if (string.IsNullOrWhiteSpace(configuredRootPath) || !Path.IsPathFullyQualified(configuredRootPath))
        {
            throw new InvalidOperationException(
                $"{RootPathKey} must be set to an absolute path in production, outside the app directory (see docs/operations/configuration.md).");
        }

        var rootPath = Path.GetFullPath(configuredRootPath);
        foreach (var appDirectory in appDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            if (IsSameOrInside(rootPath, Path.GetFullPath(appDirectory)))
            {
                throw new InvalidOperationException(
                    $"{RootPathKey} ('{rootPath}') is inside the app directory '{appDirectory}', which a release replaces; " +
                    "set it outside (see docs/operations/configuration.md).");
            }
        }

        return rootPath;
    }

    private static bool IsSameOrInside(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var dir = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        var candidate = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(dir, comparison);
    }
}
