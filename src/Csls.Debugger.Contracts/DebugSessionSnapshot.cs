namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the current observable state of one debugger target session.
/// </summary>
public sealed class DebugSessionSnapshot
{
    /// <summary>
    /// Gets the current debugger lifecycle state.
    /// </summary>
    public required DebugSessionState State { get; init; }

    /// <summary>
    /// Gets the target display name when a target has started.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Gets the target process identifier when a target has started.
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Gets the current stop reason when the target is stopped.
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>
    /// Gets the managed thread that caused the current stop when known.
    /// </summary>
    public int? StoppedThreadId { get; init; }

    /// <summary>
    /// Gets the current stop generation or zero before the first stop.
    /// </summary>
    public long StopGeneration { get; init; }

    /// <summary>
    /// Gets the target exit code after target exit.
    /// </summary>
    public int? ExitCode { get; init; }
}
