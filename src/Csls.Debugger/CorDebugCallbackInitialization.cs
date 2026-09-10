using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Completes runtime initialization after the initial managed callback queue has drained.
/// </summary>
internal sealed class CorDebugCallbackInitialization
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _processCreated;

    /// <summary>
    /// Identifies the last initial callback before its continuation resumes target execution.
    /// </summary>
    /// <param name="controller">The borrowed process or application-domain controller owned by the callback.</param>
    /// <param name="createsProcess">Whether this callback introduces the target process.</param>
    /// <returns>True when initialization can complete after this callback continues successfully.</returns>
    /// <remarks>The session actor calls this while the callback still holds its runtime stop.</remarks>
    internal unsafe bool IsFinalCallback(nint controller, bool createsProcess)
    {
        _processCreated |= createsProcess;
        if (!_processCreated || _completion.Task.IsCompleted)
        {
            return false;
        }

        int queued = 0;
        int* queuedAddress = &queued;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(controller).HasQueuedCallbacks(pThread: 0, (nint)queuedAddress),
            "ICorDebugController.HasQueuedCallbacks");
        return Volatile.Read(ref *queuedAddress) == 0;
    }

    /// <summary>
    /// Records successful continuation after the final initial callback.
    /// </summary>
    internal void Complete() => _ = _completion.TrySetResult();

    /// <summary>
    /// Ends incomplete initialization with the original runtime or callback failure.
    /// </summary>
    /// <param name="exception">The failure that prevented initialization.</param>
    internal void Fail(Exception exception) => _ = _completion.TrySetException(exception);

    /// <summary>
    /// Waits until the initial process, module, and thread notifications have been handled.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for runtime initialization.</param>
    /// <returns>A task completed after the last startup continuation succeeds.</returns>
    internal Task WaitAsync(CancellationToken cancellationToken) => _completion.Task.WaitAsync(cancellationToken);
}
