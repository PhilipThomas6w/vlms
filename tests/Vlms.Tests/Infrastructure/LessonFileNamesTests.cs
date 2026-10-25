using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>The clean-name rule and the hand-correctable list (FR-006, Philip 25/10/2026).</summary>
public sealed class LessonFileNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vlms-names-{Guid.NewGuid():N}");

    public LessonFileNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Emperor and Empress_ Drawing 07102022_095242_000115.jpg", "Emperor and Empress – Drawing")]
    [InlineData("St Theodore1_ Icon 07102022_095055_000113.jpg", "St Theodore 1 – Icon")]
    [InlineData("Noah's ark1_  Activity 07102022_095835_000119.jpg", "Noah's ark 1 – Activity")]
    [InlineData("Feasts_07102022_101217_000123.jpg", "Feasts")]
    [InlineData("Varangian Guard.docx", "Varangian Guard")]
    [InlineData("Last Supper_ Icon10102022_100428_000129.jpg", "Last Supper – Icon")]   // stamp glued on
    [InlineData("Saint1_28012023_210000_000476 (1).jpg", "Saint 1")]                     // copy marker after the stamp
    [InlineData("metania.pdf", "Metania")]                                              // first letter only
    [InlineData("A_B_C 07102022_095242_000115.jpg", "A – B – C")]                       // several underscores
    [InlineData("Icon__Drawing.png", "Icon – Drawing")]                                 // doubled underscore
    [InlineData("Plain name.pdf", "Plain name")]                                        // no stamp
    [InlineData("  Spaced   out  .pdf", "Spaced out")]
    [InlineData("_Leading_.pdf", "Leading")]
    [InlineData("St Theodore.jpg", "St Theodore")]
    public void CleanName_AppliesTheRule(string original, string expected)
        => Assert.Equal(expected, LessonFileNames.CleanName(original));

    [Fact]
    public void CleanName_OfAStampOnly_FallsBackToTheOriginalName()
        => Assert.Equal("07102022_095242_000115", LessonFileNames.CleanName("07102022_095242_000115.jpg"));

    [Fact]
    public void StoredAndDisplayNames_KeepOnlyTheExtensionInTheStoredForm()
    {
        var stored = LessonFileNames.StoredName("Emperor and Empress – Drawing", "Emperor and Empress_ Drawing 07102022_095242_000115.jpg");

        Assert.Equal("Emperor and Empress – Drawing.jpg", stored);
        Assert.Equal("Emperor and Empress – Drawing", LessonFileNames.DisplayName(stored));
        Assert.Equal("Handout", LessonFileNames.DisplayName("Handout.pdf"));
        Assert.Equal("No extension", LessonFileNames.DisplayName("No extension"));
    }

    [Fact]
    public void MergeList_AddsNewFilesByTheRule_KeepsHandEditsAndWarnsAboutMissingFiles()
    {
        var list = new Dictionary<string, FileNameEntry>
        {
            ["lessons/L/a.jpg"] = new("a.jpg", "Hand-made name"),
            ["lessons/L/gone.jpg"] = new("gone.jpg", "Gone")
        };
        var warnings = new List<string>();

        var added = LessonFileNames.MergeList(list,
            [("lessons/L/a.jpg", "a.jpg"), ("lessons/L/Feasts_07102022_101217_000123.jpg", "Feasts_07102022_101217_000123.jpg")], warnings);

        Assert.Equal(1, added);
        Assert.Equal("Hand-made name", list["lessons/L/a.jpg"].Name);
        Assert.Equal(new FileNameEntry("Feasts_07102022_101217_000123.jpg", "Feasts"), list["lessons/L/Feasts_07102022_101217_000123.jpg"]);
        Assert.Equal("Gone", list["lessons/L/gone.jpg"].Name);
        Assert.Contains(warnings, w => w.Contains("lessons/L/gone.jpg"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheListWithTheDashWrittenLiterally()
    {
        var path = Path.Combine(_dir, "file-names.json");
        var list = new Dictionary<string, FileNameEntry> { ["lessons/L/b.jpg"] = new("b_ c.jpg", "B – C") };

        LessonFileNames.SaveList(path, list);

        Assert.Contains("B – C", File.ReadAllText(path));
        var errors = new List<string>();
        Assert.Equal(list, LessonFileNames.LoadList(path, errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void LoadList_ReportsBadJsonAndBlankNames_AndTreatsAMissingFileAsEmpty()
    {
        var errors = new List<string>();
        Assert.Empty(LessonFileNames.LoadList(Path.Combine(_dir, "none.json"), errors));
        Assert.Empty(errors);

        var blank = Path.Combine(_dir, "blank.json");
        File.WriteAllText(blank, """{ "k": { "original": "x.jpg", "name": " " } }""");
        LessonFileNames.LoadList(blank, errors);
        Assert.Single(errors);

        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{");
        LessonFileNames.LoadList(bad, errors);
        Assert.Equal(2, errors.Count);
    }
}
