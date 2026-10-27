namespace Vlms.Domain;

/// <summary>
/// How a record names a person whose account has been deleted under the retention rules
/// (docs/adr/0014-retention-purge.md, settled after build 27/10/2026): the record keeps its content,
/// the link to the person is cleared, and wherever the record is shown it reads
/// <see cref="Name"/> instead of the person.
/// </summary>
public static class FormerStaff
{
    public const string Name = "a former member of staff";

    /// <summary>
    /// The name to show for a record's actor: <paramref name="name"/> when the account still exists,
    /// <see cref="Name"/> when the record says someone acted (<paramref name="acted"/>) but the
    /// account has gone, and null when nobody acted.
    /// </summary>
    public static string? Or(string? name, bool acted) => name ?? (acted ? Name : null);
}
