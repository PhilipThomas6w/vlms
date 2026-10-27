namespace Vlms.Infrastructure.Monitoring;

/// <summary>Runs one external command to completion. A seam so <see cref="SystemdStatusSource"/> is tested with a fake.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>The real runner: starts the process with no shell (arguments are passed as a list, never parsed).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var info = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await output, await error);
    }
}
