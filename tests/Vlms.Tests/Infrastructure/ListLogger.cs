using Microsoft.Extensions.Logging;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ILogger{TCategoryName}"/> test double — same spirit as
/// <see cref="InMemoryBlobStorage"/>/<see cref="FakeCurrentUserContext"/>. Records every log
/// entry's level and formatted message so tests can assert on escalation severity (e.g.
/// <see cref="Vlms.Infrastructure.Safeguarding.ConsentExpiryJob"/>'s Error-vs-Warning distinction)
/// without a mocking library.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
