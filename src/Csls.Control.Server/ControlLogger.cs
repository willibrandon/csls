using Microsoft.Extensions.Logging;

namespace Csls.Control;

/// <summary>
/// Records one logging category into the bounded control log buffer.
/// </summary>
internal sealed class ControlLogger : ILogger
{
    private readonly ControlLogBuffer _buffer;
    private readonly string _category;

    /// <summary>
    /// Creates a category logger for the bounded control log buffer.
    /// </summary>
    /// <param name="buffer">The destination log buffer.</param>
    /// <param name="category">The logging category name.</param>
    internal ControlLogger(ControlLogBuffer buffer, string category)
    {
        _buffer = buffer;
        _category = category;
    }

    /// <summary>
    /// Creates a no-op scope because dashboard logs retain rendered events only.
    /// </summary>
    /// <typeparam name="TState">The scope state type.</typeparam>
    /// <param name="state">The ignored scope state.</param>
    /// <returns>The shared no-op scope.</returns>
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => ControlLogScope.Instance;

    /// <summary>
    /// Gets whether the requested logging level is retained.
    /// </summary>
    /// <param name="logLevel">The requested logging level.</param>
    /// <returns>True unless logging is disabled for the event.</returns>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <summary>
    /// Formats and records one structured logging event.
    /// </summary>
    /// <typeparam name="TState">The logging state type.</typeparam>
    /// <param name="logLevel">The event logging level.</param>
    /// <param name="eventId">The event identifier.</param>
    /// <param name="state">The structured event state.</param>
    /// <param name="exception">The optional event exception.</param>
    /// <param name="formatter">The event formatter.</param>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        if (exception is not null)
        {
            message = string.Concat(message, ": ", exception.Message);
        }

        _buffer.Add(_category, logLevel, message);
    }
}
