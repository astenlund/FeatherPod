using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// <see cref="ILogger{TCategoryName}"/> that keeps every entry with its attached exception, so tests can assert on what a
/// service logged. Standard formatters render only the message, so an assertion on what reaches a log provider must read
/// <see cref="LogEntry.Text"/>, which also includes the exception as console and App Insights providers record it.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>
/// One captured log entry. <see cref="Text"/> is the formatted message followed by the attached exception, if any.
/// </summary>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
{
    public string Text => Exception is null ? Message : $"{Message}{Environment.NewLine}{Exception}";
}
