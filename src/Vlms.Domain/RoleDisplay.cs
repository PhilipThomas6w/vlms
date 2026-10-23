using System.Text.RegularExpressions;

namespace Vlms.Domain;

/// <summary>Reader-friendly role names for pages ("Safeguarding Officer", not the enum name "SafeguardingOfficer").</summary>
public static partial class RoleDisplay
{
    public static string DisplayName(this Role role) => SplitWords().Replace(role.ToString(), " $1");

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex SplitWords();
}
