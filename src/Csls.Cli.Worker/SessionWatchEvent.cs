using Csls.Control.Contracts;

namespace Csls.Cli.Worker;

/// <summary>
/// Describes one ordered live-session watch observation and its current snapshot.
/// </summary>
internal sealed class SessionWatchEvent
{
    /// <summary>
    /// Gets the one-based event sequence within the current watch process.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Gets how the observed live-session state changed.
    /// </summary>
    public SessionWatchEventKind Kind { get; init; }

    /// <summary>
    /// Gets the added, updated, or last-known removed session when applicable.
    /// </summary>
    public ControlSessionInfo? Session { get; init; }

    /// <summary>
    /// Gets the complete responsive live-session snapshot after this event.
    /// </summary>
    public required IReadOnlyList<ControlSessionInfo> Sessions { get; init; }
}
