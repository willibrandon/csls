using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Receives ordered lifecycle and output notifications from a debugger session.
/// </summary>
public interface IDebuggerSessionObserver
{
    /// <summary>
    /// Reports that the debugger-owned target process has started.
    /// </summary>
    /// <param name="name">The target display name.</param>
    /// <param name="processId">The operating-system process identifier.</param>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnProcessStartedAsync(
        string name,
        int processId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reports target output without merging its standard streams.
    /// </summary>
    /// <param name="category">The source target stream.</param>
    /// <param name="output">The exact output segment.</param>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnOutputAsync(
        DebugOutputCategory category,
        string output,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reports that the target entered a stable debugger stop.
    /// </summary>
    /// <param name="reason">The protocol-neutral stop reason.</param>
    /// <param name="generation">The generation owning inspection handles at this stop.</param>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnStoppedAsync(
        string reason,
        DebugStopGeneration generation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reports that debugger execution resumed for every target thread.
    /// </summary>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnContinuedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reports the target process exit code.
    /// </summary>
    /// <param name="exitCode">The operating-system process exit code.</param>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnExitedAsync(int exitCode, CancellationToken cancellationToken);

    /// <summary>
    /// Reports that no further target notifications will be produced.
    /// </summary>
    /// <param name="cancellationToken">Cancels notification delivery.</param>
    /// <returns>A task that completes after the notification is accepted.</returns>
    ValueTask OnTerminatedAsync(CancellationToken cancellationToken);
}
