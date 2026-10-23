namespace Vlms.Web.Components.Ui;

/// <summary>
/// A line of Scripture for each rank, shown under the rank's name on the student page
/// (docs/design/redesign-direction.md, sections 1 and 9). The Guard ranks follow Ephesians 6, the
/// armour of God; the later ranks follow their role in the procession; Akolouthos means follower.
/// NKJV wording, as in the Orthodox Study Bible; the Psalm is numbered as in the Septuagint. Approved by
/// Philip, 23/10/2026.
/// </summary>
public static class RankWords
{
    public sealed record Words(string Text, string Reference);

    private static readonly Dictionary<int, Words> ByRankOrder = new()
    {
        [1] = new("Stand therefore, having girded your waist with truth.", "Ephesians 6:14"),
        [2] = new("Take the helmet of salvation.", "Ephesians 6:17"),
        [3] = new("Above all, taking the shield of faith.", "Ephesians 6:16"),
        [4] = new("The sword of the Spirit, which is the word of God.", "Ephesians 6:17"),
        [5] = new("How beautiful are the feet of those who preach the gospel of peace.", "Romans 10:15"),
        [6] = new("You are the light of the world.", "Matthew 5:14"),
        [7] = new("In the name of our God we will set up our banners.", "Psalm 19:5 (20:5)"),
        [8] = new("Let him deny himself, and take up his cross, and follow Me.", "Matthew 16:24"),
        [9] = new("As many of you as were baptized into Christ have put on Christ.", "Galatians 3:27"),
        [10] = new("If anyone serves Me, let him follow Me.", "John 12:26"),
    };

    public static Words? For(int rankOrder) => ByRankOrder.GetValueOrDefault(rankOrder);
}
