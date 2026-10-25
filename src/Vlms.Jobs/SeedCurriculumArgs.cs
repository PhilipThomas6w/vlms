internal sealed record SeedCurriculumArgs(string? ManifestPath, string? ContentRoot, string? FileNamesPath, bool NamesOnly, string? Error)
{
    private const string Usage = "Usage: seed-curriculum --manifest <path> --content-root <dir> [--file-names <path>] [--names-only]";

    public static SeedCurriculumArgs Parse(string[] args)
    {
        string? manifest = null, contentRoot = null, fileNames = null;
        var namesOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--manifest" when hasValue: manifest = args[++i]; break;
                case "--content-root" when hasValue: contentRoot = args[++i]; break;
                case "--file-names" when hasValue: fileNames = args[++i]; break;
                case "--names-only": namesOnly = true; break;
                default: return new(null, null, null, false, $"Unrecognised or incomplete argument '{args[i]}'. {Usage}");
            }
        }

        if (manifest is null || contentRoot is null)
        {
            return new(null, null, null, false, $"Both --manifest and --content-root are required. {Usage}");
        }
        if (namesOnly && fileNames is null)
        {
            return new(null, null, null, false, $"--names-only needs --file-names. {Usage}");
        }

        return new(Path.GetFullPath(manifest), Path.GetFullPath(contentRoot),
            fileNames is null ? null : Path.GetFullPath(fileNames), namesOnly, null);
    }
}
