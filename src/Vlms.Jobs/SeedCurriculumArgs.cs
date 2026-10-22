/// <summary>Parsed <c>seed-curriculum</c> arguments; <see cref="Error"/> is set when they are unusable.</summary>
internal sealed record SeedCurriculumArgs(string? ManifestPath, string? ContentRoot, string? Error)
{
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

        return manifest is null || contentRoot is null
            ? new(null, null, $"Both --manifest and --content-root are required. {Usage}")
            : new(Path.GetFullPath(manifest), Path.GetFullPath(contentRoot), null);
    }
}
