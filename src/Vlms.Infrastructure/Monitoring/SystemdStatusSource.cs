using System.Globalization;

namespace Vlms.Infrastructure.Monitoring;

/// <summary>
/// The real <see cref="ISystemStatusSource"/>: <c>journalctl</c> for the service-alert lines and
/// <c>systemctl list-units</c> for failed units. The process must be allowed to read the system journal
/// (the <c>vlms</c> user needs the <c>systemd-journal</c> group; <c>deploy/systemd/vlms-alert.service</c>
/// sets it).
/// </summary>
public sealed class SystemdStatusSource(IProcessRunner runner) : ISystemStatusSource
{
    /// <summary>Marks a journal line as an operational failure for the alert digest (monitoring.md §3).</summary>
    public const string ServiceAlertMarker = "SERVICE-ALERT:";

    /// <summary>
    /// Marks a safeguarding escalation (a named student without consent, a named teacher without DBS). Those
    /// lines stay in the journal and go to each chapter's Admins and Safeguarding Officers by email; the
    /// operational digest never carries them (Philip, 29/10/2026).
    /// </summary>
    public const string SafeguardingEscalationMarker = "ESCALATION:";

    public async Task<IReadOnlyList<string>> ReadServiceAlertLinesAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("journalctl",
        [
            "--no-pager", "--quiet", "--output=short-iso",
            "--unit=vlms-web.service", "--unit=vlms-jobs.service",
            "--since=" + sinceUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            "--grep=" + ServiceAlertMarker,
        ], ct);

        // journalctl --grep exits 1 when nothing matches; that is "found nothing" only if it also said nothing.
        var matchedNothing = result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Trim().Length == 0;
        if (result.ExitCode != 0 && !matchedNothing)
        {
            throw new InvalidOperationException(
                $"journalctl exited with code {result.ExitCode}: {Truncate(result.StandardError)}");
        }

        return SplitLines(result.StandardOutput)
            .Where(line => line.Contains(ServiceAlertMarker, StringComparison.Ordinal)
                && !line.Contains(SafeguardingEscalationMarker, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListFailedUnitsAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync("systemctl",
            ["list-units", "vlms-*", "--state=failed", "--all", "--plain", "--no-legend", "--no-pager"], ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"systemctl exited with code {result.ExitCode}: {Truncate(result.StandardError)}");
        }

        // One unit per line: "<unit> loaded failed failed <description>". Some versions prefix a bullet.
        return SplitLines(result.StandardOutput)
            .Select(line => line.TrimStart('●', '*', ' ', '\t').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0 && parts[0].StartsWith("vlms-", StringComparison.Ordinal))
            .Select(parts => parts[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string text) => text.Trim() is { Length: > 300 } t ? t[..300] : text.Trim();
}
