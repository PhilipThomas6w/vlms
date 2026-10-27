using Vlms.Infrastructure.Monitoring;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>The alert digest (docs/operations/monitoring.md §3), with fakes for the host, the clock, the state and the mailer.</summary>
public sealed class AlertDigestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastRun = new(2026, 10, 27, 7, 0, 0, TimeSpan.Zero);

    private readonly FakeStatusSource _source = new();
    private readonly FakeStateStore _state = new() { Saved = new AlertState(LastRun, []) };
    private readonly FakeEmailSender _email = new();
    private readonly ListLogger<AlertDigestService> _log = new();

    private AlertDigestService Build(int maxLines = 200, int maxChars = 60_000) => new(
        _source, _state, _email,
        new AlertDigestOptions("alerts@varangians.co.uk", "unused", maxLines, maxChars, TimeSpan.FromHours(24)), _log);

    [Fact]
    public async Task NothingFound_SendsNothing_AndAdvancesTheLastRun()
    {
        var outcome = await Build().RunAsync(Now);

        Assert.Equal(AlertDigestOutcome.NothingToReport, outcome);
        Assert.Empty(_email.SentEmails);
        Assert.Equal(Now, _state.Saved!.LastRunUtc);
        Assert.Equal(LastRun, _source.LastSince);
    }

    [Fact]
    public async Task EscalationFound_SendsOneEmailToTheAlertAddress_WithTheLinesAsTheyAre()
    {
        _source.Lines.Add("2026-10-27T09:00:00+0000 vps vlms-web[1]: ESCALATION: safeguarding-critical notification to a@b.org failed after 3 attempt(s) — subject: x.");
        _source.Lines.Add("2026-10-28T03:00:00+0000 vps vlms-jobs[2]: ESCALATION: student 4 (Ann Smith) has no valid consent");

        var outcome = await Build().RunAsync(Now);

        Assert.Equal(AlertDigestOutcome.Sent, outcome);
        var sent = Assert.Single(_email.SentEmails);
        Assert.Equal("alerts@varangians.co.uk", sent.RecipientEmail);
        Assert.Contains("2 escalations", sent.Subject);
        foreach (var line in _source.Lines)
        {
            Assert.Contains(line, sent.Body);
        }
    }

    [Fact]
    public async Task NewlyFailedUnit_IsIncluded_AndNotRepeatedWhileItStaysFailed()
    {
        _source.Failed.Add("vlms-backup.service");

        await Build().RunAsync(Now);
        var first = Assert.Single(_email.SentEmails);
        Assert.Contains("vlms-backup.service", first.Body);
        Assert.Contains("1 failed unit", first.Subject);

        await Build().RunAsync(Now.AddDays(1));
        Assert.Single(_email.SentEmails);

        _source.Failed.Clear();
        await Build().RunAsync(Now.AddDays(2));
        _source.Failed.Add("vlms-backup.service");
        await Build().RunAsync(Now.AddDays(3));
        Assert.Equal(2, _email.SentEmails.Count);
    }

    [Fact]
    public async Task EmailFailure_IsLoggedAsAnError_AndTheLastRunIsNotAdvanced()
    {
        _source.Lines.Add("ESCALATION: x");
        _email.FailuresBeforeSuccess = int.MaxValue;

        var outcome = await Build().RunAsync(Now);

        Assert.Equal(AlertDigestOutcome.Failed, outcome);
        Assert.Equal(LastRun, _state.Saved!.LastRunUtc);
        Assert.Contains(_log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);

        // The next run, with the mailer working again, covers the same period and sends.
        _email.FailuresBeforeSuccess = 0;
        Assert.Equal(AlertDigestOutcome.Sent, await Build().RunAsync(Now.AddDays(1)));
        Assert.Equal(LastRun, _source.LastSince);
        Assert.Equal(Now.AddDays(1), _state.Saved!.LastRunUtc);
    }

    [Fact]
    public async Task HostReadFailure_IsLoggedAsAnError_NeverReportedAsNothingFound()
    {
        _source.Throw = true;

        var outcome = await Build().RunAsync(Now);

        Assert.Equal(AlertDigestOutcome.Failed, outcome);
        Assert.Empty(_email.SentEmails);
        Assert.Equal(LastRun, _state.Saved!.LastRunUtc);
        Assert.Contains(_log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task FirstRun_LooksBackTheConfiguredPeriod()
    {
        _state.Saved = null;

        await Build().RunAsync(Now);

        Assert.Equal(Now - TimeSpan.FromHours(24), _source.LastSince);
        Assert.Equal(Now, _state.Saved!.LastRunUtc);
    }

    [Fact]
    public async Task LongOutput_IsCapped_WithANoteOfWhatWasLeftOut()
    {
        for (var i = 0; i < 50; i++)
        {
            _source.Lines.Add($"ESCALATION: line {i}");
        }

        await Build(maxLines: 10).RunAsync(Now);

        var body = Assert.Single(_email.SentEmails).Body;
        Assert.Contains("ESCALATION: line 9", body);
        Assert.DoesNotContain("ESCALATION: line 10\n", body);
        Assert.Contains("40 more line(s) not shown", body);
    }

    [Fact]
    public async Task FileStateStore_RoundTrips_AndReportsNullBeforeTheFirstSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "vlms-alert-" + Guid.NewGuid().ToString("N"), "alert-state.json");
        try
        {
            var store = new FileAlertStateStore(path);
            Assert.Null(await store.LoadAsync());

            await store.SaveAsync(new AlertState(Now, ["vlms-backup.service"]));
            var loaded = await store.LoadAsync();

            Assert.Equal(Now, loaded!.LastRunUtc);
            Assert.Equal(["vlms-backup.service"], loaded.ReportedFailedUnits);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed class FakeStatusSource : ISystemStatusSource
    {
        public List<string> Lines { get; } = [];
        public List<string> Failed { get; } = [];
        public DateTimeOffset? LastSince { get; private set; }
        public bool Throw { get; set; }

        public Task<IReadOnlyList<string>> ReadEscalationLinesAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
        {
            LastSince = sinceUtc;
            return Throw ? throw new InvalidOperationException("journalctl exited with code 2") : Task.FromResult<IReadOnlyList<string>>(Lines.ToList());
        }

        public Task<IReadOnlyList<string>> ListFailedUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Failed.ToList());
    }

    private sealed class FakeStateStore : IAlertStateStore
    {
        public AlertState? Saved { get; set; }
        public Task<AlertState?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(AlertState state, CancellationToken ct = default) { Saved = state; return Task.CompletedTask; }
    }
}

/// <summary><see cref="SystemdStatusSource"/> against a fake process runner: the commands it issues and how it reads their output.</summary>
public sealed class SystemdStatusSourceTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 27, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Journal_AsksForBothUnitsAndTheMarker_SinceTheGivenTimeInUtc()
    {
        var runner = new FakeRunner(new ProcessResult(0, "a ESCALATION: one\nb ESCALATION: two\n", ""));

        var lines = await new SystemdStatusSource(runner).ReadEscalationLinesAsync(Since);

        Assert.Equal(2, lines.Count);
        Assert.Equal("journalctl", runner.FileName);
        Assert.Contains("--unit=vlms-web.service", runner.Arguments);
        Assert.Contains("--unit=vlms-jobs.service", runner.Arguments);
        Assert.Contains("--grep=ESCALATION:", runner.Arguments);
        Assert.Contains("--since=2026-10-27 07:00:00 UTC", runner.Arguments);
    }

    [Fact]
    public async Task Journal_ExitOneWithNoOutput_IsNothingFound()
    {
        var lines = await new SystemdStatusSource(new FakeRunner(new ProcessResult(1, "", ""))).ReadEscalationLinesAsync(Since);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Journal_ExitOneWithAnError_Throws()
    {
        var source = new SystemdStatusSource(new FakeRunner(new ProcessResult(1, "", "Failed to open journal: Permission denied")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadEscalationLinesAsync(Since));
    }

    [Fact]
    public async Task FailedUnits_AreParsedFromListUnitsOutput()
    {
        var output = "vlms-backup.service loaded failed failed VLMS database backup\n● vlms-jobs.service loaded failed failed VLMS jobs\n";

        var units = await new SystemdStatusSource(new FakeRunner(new ProcessResult(0, output, ""))).ListFailedUnitsAsync();

        Assert.Equal(["vlms-backup.service", "vlms-jobs.service"], units);
    }

    [Fact]
    public async Task FailedUnits_NonZeroExit_Throws()
    {
        var source = new SystemdStatusSource(new FakeRunner(new ProcessResult(1, "", "boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListFailedUnitsAsync());
    }

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }
}
