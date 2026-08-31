namespace Csls.Tests;

/// <summary>
/// Identifies one process and the exit observation started while it was running.
/// </summary>
internal readonly record struct ProcessExitObservation
{
    /// <summary>
    /// Initializes an exact process exit observation.
    /// </summary>
    /// <param name="processId">The observed operating-system process identifier.</param>
    /// <param name="exitTask">The process exit task started during observation.</param>
    internal ProcessExitObservation(int processId, Task exitTask)
    {
        ProcessId = processId;
        ExitTask = exitTask;
    }

    /// <summary>
    /// Gets the observed operating-system process identifier.
    /// </summary>
    internal int ProcessId { get; }

    /// <summary>
    /// Gets the task registered to complete when the observed process exits.
    /// </summary>
    internal Task ExitTask { get; }
}
