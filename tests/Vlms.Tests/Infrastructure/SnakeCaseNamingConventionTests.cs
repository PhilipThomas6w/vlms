using Vlms.Infrastructure.Naming;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// <see cref="SnakeCaseNamingConvention.ToSnakeCase"/> is the pure function behind
/// adr/0005-hosting-pivot-self-hosted-vps-postgresql.md's identifier-casing decision — see
/// openwiki/data-access.md. Tested directly (no DbContext/model needed) rather than only indirectly
/// through a generated migration script, since the acronym-handling boundary case
/// (<c>ConsentSensitiveDetails.DietarySEN</c> — a real property in this schema, not a hypothetical)
/// is exactly the kind of thing a naive single-regex approach gets wrong.
/// </summary>
public class SnakeCaseNamingConventionTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("Rank", "rank")]
    [InlineData("AppUserId", "app_user_id")]
    [InlineData("ParentGuardianId", "parent_guardian_id")]
    [InlineData("SensitiveDataAccessLogs", "sensitive_data_access_logs")]
    [InlineData("DbsCheck", "dbs_check")]
    [InlineData("DbsChecks", "dbs_checks")]
    [InlineData("ConsentSensitiveDetails", "consent_sensitive_details")]
    [InlineData("StudentGuardianLinks", "student_guardian_links")]
    [InlineData("StudentRankProgresses", "student_rank_progresses")]
    public void ToSnakeCase_ConvertsPascalCaseIdentifiers(string input, string expected)
    {
        Assert.Equal(expected, SnakeCaseNamingConvention.ToSnakeCase(input));
    }

    [Fact]
    public void ToSnakeCase_HandlesTrailingAcronymAsOneWord()
    {
        // The real, load-bearing case: a trailing all-caps acronym must render as a single
        // trailing word ("sen"), not one underscore-separated letter per character. A naive
        // "insert underscore before every uppercase letter" regex would produce "dietary_s_e_n".
        Assert.Equal("dietary_sen", SnakeCaseNamingConvention.ToSnakeCase("DietarySEN"));
    }

    [Fact]
    public void ToSnakeCase_HandlesLeadingAcronymFollowedByWord()
    {
        // The other half of the acronym boundary: an acronym immediately followed by a
        // capitalized word must split before the word starts, not after the acronym's first
        // letter — "SENProperty" -> "sen_property", not "s_e_n_property" or "senproperty".
        Assert.Equal("sen_property", SnakeCaseNamingConvention.ToSnakeCase("SENProperty"));
    }

    [Fact]
    public void ToSnakeCase_IsIdempotent()
    {
        var once = SnakeCaseNamingConvention.ToSnakeCase("SensitiveDataAccessLogs");
        var twice = SnakeCaseNamingConvention.ToSnakeCase(once);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ToSnakeCase_HandlesNullOrEmpty(string? input)
    {
        Assert.Equal(input, SnakeCaseNamingConvention.ToSnakeCase(input!));
    }
}
