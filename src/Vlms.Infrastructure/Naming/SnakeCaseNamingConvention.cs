using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Vlms.Infrastructure.Naming;

/// <summary>
/// Applies PostgreSQL's own idiomatic snake_case identifier convention to every table and column
/// name in the model — the casing decision <c>adr/0005-hosting-pivot-self-hosted-vps-postgresql.md</c>
/// flagged as "confirm/apply", not pre-decided. Called once, at the end of
/// <see cref="VlmsDbContext.OnModelCreating"/>, after every other Fluent API call has run (so the
/// PascalCase defaults it renames are already fully assigned).
///
/// **Scope, deliberately limited:** only table and column names are renamed. Primary key/foreign
/// key/index constraint names are left at EF Core's PascalCase defaults (e.g. <c>PK_Ranks</c>,
/// <c>FK_Lessons_Ranks_RankId</c>) — they are internal, essentially never typed by hand, and
/// renaming them adds real complexity (constraint-name-length limits, composite-key naming) for no
/// benefit any part of this codebase or its docs actually needs. This is a scoped decision, not an
/// oversight.
///
/// **Why a hand-rolled helper instead of the community `EFCore.NamingConventions` package:** this is
/// a solo-maintained, "fewest moving parts" build (adr/0003's reasoning, already applied elsewhere
/// to reject heavier tooling) and the actual need — rename tables/columns consistently — is small
/// and fully covered by the loop below. Adding a third-party package for ~30 lines of mechanical
/// renaming was judged not worth the extra dependency.
///
/// **Table name source:** this context declares its <c>DbSet&lt;T&gt;</c> properties with explicit
/// plural names (e.g. <c>DbSet&lt;SensitiveDataAccessLog&gt; SensitiveDataAccessLogs</c>), and EF
/// Core's default table-naming convention uses that property name — so
/// <c>entity.GetTableName()</c> already returns e.g. <c>"SensitiveDataAccessLogs"</c> by the time
/// this runs, and <see cref="ToSnakeCase"/> renders it <c>sensitive_data_access_logs</c> (plural,
/// snake_case) — not the singular <c>sensitive_data_access_log</c> ADR-0005 used as an illustrative
/// example; that ADR text was explicitly non-binding on this point, and the plural form keeps every
/// table name consistent with the existing pluralized SQL Server DDL/migration history rather than
/// introducing a singular/plural split across the schema.
/// </summary>
public static class SnakeCaseNamingConvention
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    /// <summary>
    /// Converts a PascalCase/camelCase .NET identifier to snake_case, correctly handling
    /// consecutive-uppercase acronyms (e.g. <c>DietarySEN</c> -&gt; <c>dietary_sen</c>, not
    /// <c>dietary_s_e_n</c> — <c>ConsentSensitiveDetails.DietarySEN</c> is this schema's own real
    /// case, not a hypothetical). This is the standard word-boundary algorithm used by the
    /// EFCore.NamingConventions package and similar tools: an underscore is inserted before an
    /// uppercase letter only when it starts a new word — i.e. the previous character was lowercase
    /// or a digit, or the previous character was itself uppercase but the one after the current
    /// character is lowercase (the acronym-to-word boundary, e.g. the "N" in "SENProperty").
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 5);
        UnicodeCategory? previousCategory = null;

        for (var i = 0; i < name.Length; i++)
        {
            var currentChar = name[i];

            if (currentChar == '_')
            {
                builder.Append('_');
                previousCategory = null;
                continue;
            }

            var currentCategory = char.IsUpper(currentChar)
                ? UnicodeCategory.UppercaseLetter
                : char.IsLower(currentChar)
                    ? UnicodeCategory.LowercaseLetter
                    : char.IsDigit(currentChar)
                        ? UnicodeCategory.DecimalDigitNumber
                        : CharUnicodeInfo.GetUnicodeCategory(currentChar);

            if (currentCategory == UnicodeCategory.UppercaseLetter)
            {
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                var startsNewWord =
                    previousCategory == UnicodeCategory.LowercaseLetter ||
                    previousCategory == UnicodeCategory.DecimalDigitNumber ||
                    (previousCategory == UnicodeCategory.UppercaseLetter && nextIsLower);

                if (i > 0 && startsNewWord)
                {
                    builder.Append('_');
                }

                currentChar = char.ToLowerInvariant(currentChar);
            }

            builder.Append(currentChar);
            previousCategory = currentCategory;
        }

        return builder.ToString();
    }
}
