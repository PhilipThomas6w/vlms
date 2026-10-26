using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>One entry of <c>seed/file-names.json</c>: the file's original name and the clean name (no extension) shown for it.</summary>
public sealed record FileNameEntry(string Original, string Name);

/// <summary>
/// Clean lesson file names (FR-006, Philip 25/10/2026). Lesson files came from a scanner with names such
/// as <c>Emperor and Empress_ Drawing 07102022_095242_000115.jpg</c>; the library, the saved-lessons page and
/// downloads show "Emperor and Empress – Drawing" instead, with no file type shown. The stored
/// <c>LessonFile.FileName</c> is the clean name plus the original extension, so downloads still open in
/// the right app; <see cref="DisplayName"/> drops the extension again for display.
/// </summary>
public static partial class LessonFileNames
{
    /// <summary>
    /// The scanner's stamp at the end of a name: date, time, sequence, optionally glued on. A Windows
    /// copy marker after it ("... 000476 (1)") goes with it: the real names include two such files.
    /// </summary>
    [GeneratedRegex(@"[_\s]*\d{8}_\d{6}_\d{6}(\s*\(\d+\))?$")]
    private static partial Regex ScannerStamp();

    [GeneratedRegex(@"(?<=\p{L})(?=\d)")]
    private static partial Regex LetterThenDigit();

    [GeneratedRegex(@"\s*_+\s*")]
    private static partial Regex Underscores();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The rule: drop the extension, remove a trailing scanner stamp, put a space between a letter and
    /// following digits, turn underscores into " – ", collapse whitespace, trim spaces and dashes and
    /// capitalise the first letter only. Returns the name without extension; if nothing is left (a name
    /// that was only a stamp) the original name without extension is returned instead.
    /// </summary>
    public static string CleanName(string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var name = ScannerStamp().Replace(stem, "");
        name = LetterThenDigit().Replace(name, " ");
        name = Underscores().Replace(name, " – ");
        name = Whitespace().Replace(name, " ").Trim(' ', '–', '-');
        if (name.Length == 0)
        {
            return stem.Trim();
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>The name stored in <c>LessonFile.FileName</c>: the clean name plus the original extension.</summary>
    public static string StoredName(string cleanName, string originalFileName)
        => cleanName + Path.GetExtension(originalFileName);

    /// <summary>A stored file name without its extension, as shown in the library and on the saved-lessons page.</summary>
    public static string DisplayName(string storedFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(storedFileName);
        return stem.Length == 0 ? storedFileName : stem;
    }

    // --- The hand-correctable list (seed/file-names.json) ---------------------------------------

    private static readonly JsonSerializerOptions ListOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Write the en dash literally so the list is readable and editable by hand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Reads the list, keyed by blob key. A missing file is an empty list; unreadable JSON is an error.</summary>
    public static Dictionary<string, FileNameEntry> LoadList(string path, List<string> errors)
    {
        var list = new Dictionary<string, FileNameEntry>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return list;
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, FileNameEntry>>(File.ReadAllText(path), ListOptions);
            foreach (var (key, entry) in read ?? [])
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
                {
                    errors.Add($"File-name list '{path}': entry '{key}' has no name.");
                }
                else
                {
                    list[key] = entry;
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"File-name list is not valid JSON: {path}: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Adds a rule-made entry for every file not yet listed. Existing entries (hand-corrected or not) are
    /// never touched and never removed; entries for files no longer present are reported as warnings.
    /// Returns the number of entries added.
    /// </summary>
    public static int MergeList(Dictionary<string, FileNameEntry> list, IEnumerable<(string BlobKey, string OriginalName)> files, List<string> warnings)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        foreach (var (blobKey, originalName) in files)
        {
            present.Add(blobKey);
            if (!list.ContainsKey(blobKey))
            {
                list[blobKey] = new FileNameEntry(originalName, CleanName(originalName));
                added++;
            }
        }

        warnings.AddRange(list.Keys.Where(k => !present.Contains(k)).Order(StringComparer.Ordinal)
            .Select(k => $"File-name list entry '{k}' is for a file no longer present; kept."));
        return added;
    }

    /// <summary>Writes the list (UTF-8, no BOM), sorted by blob key so diffs stay small.</summary>
    public static void SaveList(string path, Dictionary<string, FileNameEntry> list)
    {
        var sorted = new SortedDictionary<string, FileNameEntry>(list, StringComparer.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, ListOptions) + Environment.NewLine, new UTF8Encoding(false));
    }
}
