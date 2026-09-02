namespace Csls.Debugger;

/// <summary>
/// Defines the process and stream operations shared by debugger-owned targets.
/// </summary>
internal interface IDebuggeeProcess : IAsyncDisposable
{
    /// <summary>
    /// Gets the operating-system process identifier.
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Gets the display name of the target process.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets whether the debugger created and owns the target process.
    /// </summary>
    bool OwnsProcess { get; }

    /// <summary>
    /// Copies target standard output to a debugger callback until end of stream.
    /// </summary>
    /// <param name="writeAsync">Receives each output segment.</param>
    /// <param name="cancellationToken">Cancels output collection.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    Task CopyStandardOutputAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken);

    /// <summary>
    /// Copies target standard error to a debugger callback until end of stream.
    /// </summary>
    /// <param name="writeAsync">Receives each output segment.</param>
    /// <param name="cancellationToken">Cancels output collection.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    Task CopyStandardErrorAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the target and returns its exit code.
    /// </summary>
    /// <param name="cancellationToken">Cancels only the wait operation.</param>
    /// <returns>The target exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the target and its descendants when it is still running.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for process exit.</param>
    /// <returns>A task that completes after the target exits.</returns>
    Task TerminateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Relinquishes debugger ownership without terminating the target.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for detachment.</param>
    /// <returns>A task that completes after debugger ownership is released.</returns>
    Task DetachAsync(CancellationToken cancellationToken);
}
