using Microsoft.Extensions.Logging;

namespace Csls.Control;

/// <summary>
/// Provides loggers backed by one bounded in-memory session log buffer.
/// </summary>
public sealed class ControlLogBuffer : ILoggerProvider
{
    private const int MaximumEntries = 200;
    private readonly Lock _gate = new();
    private readonly Queue<ControlLogRecord> _entries = new(MaximumEntries);
    private long _sequence;

    /// <summary>
    /// Creates a logger that records messages under one category.
    /// </summary>
    /// <param name="categoryName">The logging category name.</param>
    /// <returns>The category logger.</returns>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new ControlLogger(this, categoryName);
    }

    /// <summary>
    /// Releases the provider without affecting already captured immutable entries.
    /// </summary>
    public void Dispose() => GC.SuppressFinalize(this);

    /// <summary>
    /// Returns the currently retained log entries in monotonic order.
    /// </summary>
    /// <returns>The immutable bounded log records.</returns>
    internal IReadOnlyList<ControlLogRecord> GetSnapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>
    /// Adds one formatted log entry while enforcing the configured bound.
    /// </summary>
    /// <param name="category">The logging category name.</param>
    /// <param name="level">The logging level.</param>
    /// <param name="message">The formatted message.</param>
    internal void Add(string category, LogLevel level, string message)
    {
        var entry = new ControlLogRecord
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
            Level = level,
            Message = message
        };
        lock (_gate)
        {
            if (_entries.Count == MaximumEntries)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }
}
