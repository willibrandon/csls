using Microsoft.Extensions.Logging;

namespace Csls.Server;

/// <summary>
/// Applies the current language-server minimum level to every logging provider.
/// </summary>
public sealed class LanguageServerLogFilter
{
    private int _minimumLevel = (int)LogLevel.Information;

    /// <summary>
    /// Gets the current minimum logging level.
    /// </summary>
    public LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

    /// <summary>
    /// Determines whether an event at the supplied level should be written.
    /// </summary>
    /// <param name="level">The event logging level.</param>
    /// <returns>True when the event passes the current minimum level.</returns>
    public bool IsEnabled(LogLevel level)
    {
        LogLevel minimumLevel = MinimumLevel;
        return minimumLevel is not LogLevel.None && level >= minimumLevel;
    }

    /// <summary>
    /// Replaces the minimum logging level for subsequent events.
    /// </summary>
    /// <param name="minimumLevel">The new minimum logging level.</param>
    public void SetMinimumLevel(LogLevel minimumLevel)
    {
        if (minimumLevel is < LogLevel.Trace or > LogLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        }

        Volatile.Write(ref _minimumLevel, (int)minimumLevel);
    }
}
