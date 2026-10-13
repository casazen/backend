using Microsoft.Extensions.Logging;

namespace Casazen.Tests.Unit.Logging;

/// <summary>Fake log sink: keeps every rendered message and its structured values, as a real provider would get them.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<CapturedLogEntry> Entries { get; } = [];

    /// <summary>Everything a log provider could write: rendered messages, structured values and exceptions.</summary>
    public string AllOutput => string.Join(
        Environment.NewLine,
        Entries.Select(e => $"{e.Message} | {string.Join(", ", e.Values.Select(v => $"{v.Key}={v.Value}"))} | {e.Exception}"));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entries.Add(new CapturedLogEntry(
            logLevel,
            formatter(state, exception),
            values.ToDictionary(v => v.Key, v => v.Value),
            exception));
    }
}

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Values,
    Exception? Exception);
