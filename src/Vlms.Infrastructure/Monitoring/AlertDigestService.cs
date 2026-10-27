using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vlms.Infrastructure.Notifications;

namespace Vlms.Infrastructure.Monitoring;

/// <summary>
/// Settings for <see cref="AlertDigestService"/>, bound from the <c>Alerts</c> configuration section of
/// the jobs service. Not secrets.
/// </summary>
public sealed record AlertDigestOptions(
    string Recipient, string StatePath, int MaxLines, int MaxBodyCharacters, TimeSpan FirstRunLookback)
{
    public const string Section = "Alerts";
    public const string DefaultRecipient = "alerts@varangians.co.uk";
    public const string StateFileName = "alert-state.json";

    public static AlertDigestOptions FromConfiguration(IConfiguration configuration, string fallbackDirectory)
    {
        var recipient = configuration[$"{Section}:Recipient"];
        var statePath = configuration[$"{Section}:StatePath"];
        if (string.IsNullOrWhiteSpace(statePath))
        {
            // systemd sets STATE_DIRECTORY for a unit with StateDirectory= (vlms-alert.service).
            var directory = Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0];
            statePath = Path.Combine(string.IsNullOrWhiteSpace(directory) ? fallbackDirectory : directory, StateFileName);
        }

        return new AlertDigestOptions(
            string.IsNullOrWhiteSpace(recipient) ? DefaultRecipient : recipient.Trim(),
            statePath,
            configuration.GetValue($"{Section}:MaxLines", 200),
            configuration.GetValue($"{Section}:MaxBodyCharacters", 60_000),
            TimeSpan.FromHours(configuration.GetValue($"{Section}:FirstRunLookbackHours", 24)));
    }
}

/// <summary>What the digest remembers between runs.</summary>
public sealed record AlertState(DateTimeOffset LastRunUtc, IReadOnlyList<string> ReportedFailedUnits);

/// <summary>Where <see cref="AlertState"/> is kept. A seam so tests run without a disk.</summary>
public interface IAlertStateStore
{
    /// <summary>The saved state, or null on the very first run. Throws if the file exists but cannot be read.</summary>
    Task<AlertState?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AlertState state, CancellationToken ct = default);
}

/// <summary>A small JSON file under the alert service's state directory. Written to a temp file and moved into place.</summary>
public sealed class FileAlertStateStore(string path) : IAlertStateStore
{
    public async Task<AlertState?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AlertState>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException($"Alert state file '{path}' is empty.");
    }

    public async Task SaveAsync(AlertState state, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), ct);
        File.Move(temporary, path, overwrite: true);
    }
}

public enum AlertDigestOutcome
{
    /// <summary>Nothing new was found; nothing was sent.</summary>
    NothingToReport,

    /// <summary>One email was sent.</summary>
    Sent,

    /// <summary>The host could not be read, the email could not be sent or the state could not be saved. Logged as an error.</summary>
    Failed,
}

/// <summary>
/// The alert route decided 27/10/2026 (docs/operations/monitoring.md §3): collects, since the last
/// successful run, the journal's <c>ESCALATION:</c> lines and any <c>vlms-*</c> unit that has newly failed,
/// and emails them in one message to the alert address through the app's email sender. Nothing found,
/// nothing sent. The last-run time moves forward only after the email was accepted (or there was nothing to
/// send), so a failed run is retried over the same window. Never silent: every failure is logged as an error
/// and the outcome is <see cref="AlertDigestOutcome.Failed"/> (the host exits non-zero, which makes the
/// run's own unit show as failed).
///
/// A unit that is still failed from an earlier report is not reported again until it recovers; a unit that
/// fails a second time while it is still sitting in the failed state is therefore not re-reported (the
/// weekly look at <c>systemctl --failed</c> is the backstop).
///
/// The journal lines are sent as they are: they hold at most the names and email addresses the application
/// already logs, to a mailbox inside the same tenant. The size is capped (lines and characters) with a note
/// of what was left out.
/// </summary>
public sealed class AlertDigestService(
    ISystemStatusSource source, IAlertStateStore state, IEmailSender email, AlertDigestOptions options,
    ILogger<AlertDigestService> logger)
{
    private const int MaxLineLength = 1000;

    public async Task<AlertDigestOutcome> RunAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        try
        {
            var previous = await state.LoadAsync(ct);
            var since = previous?.LastRunUtc ?? nowUtc - options.FirstRunLookback;

            var lines = await source.ReadEscalationLinesAsync(since, ct);
            var failedUnits = await source.ListFailedUnitsAsync(ct);
            var alreadyReported = previous?.ReportedFailedUnits ?? [];
            var newlyFailed = failedUnits.Where(unit => !alreadyReported.Contains(unit, StringComparer.Ordinal)).ToList();

            var outcome = AlertDigestOutcome.NothingToReport;
            if (lines.Count > 0 || newlyFailed.Count > 0)
            {
                await email.SendAsync(options.Recipient, "VLMS alerts", BuildSubject(lines.Count, newlyFailed.Count),
                    BuildBody(since, nowUtc, lines, newlyFailed), ct);
                outcome = AlertDigestOutcome.Sent;
                logger.LogInformation(
                    "Alert digest sent: {LineCount} journal line(s), {UnitCount} newly failed unit(s).", lines.Count, newlyFailed.Count);
            }

            // Only now: a failure above leaves the old state, so the next run covers the same window again.
            await state.SaveAsync(new AlertState(nowUtc, failedUnits), ct);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Alert digest failed; nothing was marked as reported, so the next run covers the same period. Check the journal and the alert email setup by hand.");
            return AlertDigestOutcome.Failed;
        }
    }

    private static string BuildSubject(int lineCount, int unitCount)
    {
        var parts = new List<string>();
        if (lineCount > 0)
        {
            parts.Add(lineCount == 1 ? "1 escalation" : $"{lineCount} escalations");
        }
        if (unitCount > 0)
        {
            parts.Add(unitCount == 1 ? "1 failed unit" : $"{unitCount} failed units");
        }

        return "VLMS alert: " + string.Join(", ", parts);
    }

    private string BuildBody(DateTimeOffset since, DateTimeOffset now, IReadOnlyList<string> lines, IReadOnlyList<string> newlyFailed)
    {
        var body = new StringBuilder();
        body.Append("Period: ").Append(Stamp(since)).Append(" to ").Append(Stamp(now)).Append(" UTC\n");

        if (newlyFailed.Count > 0)
        {
            body.Append("\nFailed systemd units (check with: systemctl status <unit>; journalctl -u <unit>):\n");
            foreach (var unit in newlyFailed)
            {
                body.Append("  ").Append(unit).Append('\n');
            }
        }

        if (lines.Count > 0)
        {
            body.Append("\nEscalation lines from vlms-web.service and vlms-jobs.service:\n");
            var shown = 0;
            foreach (var line in lines.Take(options.MaxLines))
            {
                var text = line.Length > MaxLineLength ? line[..MaxLineLength] + " [line cut]" : line;
                if (body.Length + text.Length + 1 > options.MaxBodyCharacters)
                {
                    break;
                }

                body.Append(text).Append('\n');
                shown++;
            }

            if (shown < lines.Count)
            {
                body.Append($"\n[{lines.Count - shown} more line(s) not shown. Read them on the server: journalctl -u vlms-web.service -u vlms-jobs.service -g ESCALATION --since \"{Stamp(since)} UTC\"]\n");
            }
        }

        return body.ToString();
    }

    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
