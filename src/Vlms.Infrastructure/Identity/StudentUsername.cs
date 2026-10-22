using System.Text.RegularExpressions;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// The username rule for a student sign-in (functional.md FR-011, adr/0007-student-sign-in-accounts.md):
/// 3 to 32 characters, letters, digits, '.', '-' and '_' only, starting with a letter or digit.
///
/// No '@' is allowed, so a student username can never have an email shape. Every adult account's
/// username is its email address (<see cref="AppUserProvisioningService.CreateAsync"/>), which
/// always contains '@', so the two namespaces cannot collide. Uniqueness itself is Identity's own:
/// the unique index on <c>normalized_user_name</c>, compared case-insensitively.
/// </summary>
public static partial class StudentUsername
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    public const string RuleDescription =
        "Usernames are 3 to 32 characters: letters, numbers, full stops, hyphens and underscores, starting with a letter or number. No email addresses.";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>True when <paramref name="username"/> (already trimmed) meets the rule.</summary>
    public static bool IsValid(string? username) => username is not null && Pattern().IsMatch(username);
}
