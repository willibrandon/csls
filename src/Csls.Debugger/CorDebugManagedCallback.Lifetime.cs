using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Exposes and releases the native managed-callback lifetime.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    /// <summary>
    /// Gets the COM interface pointer accepted by ICorDebug.SetManagedHandler.
    /// </summary>
    internal nint Pointer => Volatile.Read(ref _instance);

    /// <summary>
    /// Waits until CoreCLR reports and resumes the initial create-process stop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the initial managed callback.</param>
    /// <returns>A task that completes after the runtime accepts Continue.</returns>
    internal async Task WaitForCreateProcessAsync(CancellationToken cancellationToken)
    {
        int result = await _createProcessCompletion.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Continue");
    }

    /// <summary>
    /// Waits until CoreCLR delivers the terminal process callback on the engine actor.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for callback delivery.</param>
    /// <returns>A task that completes after the callback relinquishes its process pointer.</returns>
    internal Task WaitForExitProcessAsync(CancellationToken cancellationToken) =>
        _exitProcessCompletion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        nint instance = Interlocked.Exchange(ref _instance, 0);
        if (instance != 0)
        {
            _ = ReleaseCore(instance);
        }
    }
}
