namespace Vlms.Infrastructure.Monitoring;

/// <summary>
/// Where the alert digest reads the host's journal and unit states from. A seam so the digest logic is
/// tested with fakes on any operating system; the real implementation (<see cref="SystemdStatusSource"/>)
/// runs <c>journalctl</c> and <c>systemctl</c> on the VPS. Both methods throw if the host could not be
/// read: a failure to look is never reported as "nothing found".
/// </summary>
public interface ISystemStatusSource
{
    /// <summary>
    /// Journal lines containing <c>ESCALATION:</c> written by <c>vlms-web.service</c> and
    /// <c>vlms-jobs.service</c> since <paramref name="sinceUtc"/>, oldest first, as the journal prints them.
    /// </summary>
    Task<IReadOnlyList<string>> ReadEscalationLinesAsync(DateTimeOffset sinceUtc, CancellationToken ct = default);

    /// <summary>The names of the <c>vlms-*</c> systemd units currently in the failed state.</summary>
    Task<IReadOnlyList<string>> ListFailedUnitsAsync(CancellationToken ct = default);
}
