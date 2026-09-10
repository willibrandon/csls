namespace Csls.Debugger;

/// <summary>
/// Enforces one live ICorDebug owner per debugger host process.
/// </summary>
internal static class CorDebugRuntimeActivationGate
{
    private static readonly SemaphoreSlim s_gate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Waits for exclusive ownership of the process-wide ICorDebug runtime.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting before the handshake begins.</param>
    /// <returns>A task that completes when activation ownership is acquired.</returns>
    internal static Task WaitAsync(CancellationToken cancellationToken) =>
        s_gate.WaitAsync(cancellationToken);

    /// <summary>
    /// Releases runtime ownership after the managed debuggee is fully disposed.
    /// </summary>
    internal static void Release() => _ = s_gate.Release();
}
