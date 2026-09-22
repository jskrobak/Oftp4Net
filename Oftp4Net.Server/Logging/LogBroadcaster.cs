using System.Collections.Concurrent;

namespace Oftp4Net.Server.Logging;

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message);

/// <summary>
/// Keeps the most recent log entries in memory and notifies subscribers (the Log page) about new ones.
/// </summary>
public sealed class LogBroadcaster
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> GetRecent() => _entries.ToArray();

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity)
            _entries.TryDequeue(out _);

        EntryAdded?.Invoke(entry);
    }
}

/// <summary>Forwards log messages of the application (and framework warnings) to <see cref="LogBroadcaster"/>.</summary>
public sealed class BroadcastLoggerProvider(LogBroadcaster broadcaster) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BroadcastLogger(broadcaster, categoryName);

    public void Dispose()
    {
    }

    private sealed class BroadcastLogger(LogBroadcaster broadcaster, string category) : ILogger
    {
        private readonly bool _isApplication = category.StartsWith("Oftp4Net", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= (_isApplication ? LogLevel.Debug : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception is not null)
                message += " | " + exception.Message;

            broadcaster.Add(new LogEntry(DateTime.Now, logLevel, category, message));
        }
    }
}
