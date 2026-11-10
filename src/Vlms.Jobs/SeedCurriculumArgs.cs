internal sealed record SeedCurriculumArgs(string? ManifestPath, string? ContentRoot, string? Error)
{
    // --file-names and --names-only were removed with docs/adr/0016: the seeder no longer reads the original rank folders,
    // so the file-name list they maintained has nothing to name. Passing either is refused as an unrecognised argument.
    private const string Usage = "Usage: seed-curriculum --manifest <path> --content-root <dir>";

    public static SeedCurriculumArgs Parse(string[] args)
    {
        string? manifest = null, contentRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--manifest" when hasValue: manifest = args[++i]; break;
                case "--content-root" when hasValue: contentRoot = args[++i]; break;
                default: return new(null, null, $"Unrecognised or incomplete argument '{args[i]}'. {Usage}");
            }
        }

        if (manifest is null || contentRoot is null)
        {
            return new(null, null, $"Both --manifest and --content-root are required. {Usage}");
        }

        return new(Path.GetFullPath(manifest), Path.GetFullPath(contentRoot), null);
    }
}
