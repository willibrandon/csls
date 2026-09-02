namespace Csls.Debugger;

/// <summary>
/// Bounds process-wide CoreCLR activation so concurrent sessions do not exhaust startup handshakes.
/// </summary>
internal static class CorDebugRuntimeActivationGate
{
    private static readonly SemaphoreSlim s_gate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Waits for exclusive ownership of one CoreCLR activation handshake.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting before the handshake begins.</param>
    /// <returns>A task that completes when activation ownership is acquired.</returns>
    internal static Task WaitAsync(CancellationToken cancellationToken) =>
        s_gate.WaitAsync(cancellationToken);

    /// <summary>
    /// Releases ownership after the runtime and initial managed callback are ready.
    /// </summary>
    internal static void Release() => _ = s_gate.Release();
}
