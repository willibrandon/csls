using Csls.Debugger.Contracts;

namespace Csls.Debugger.StackProbe;

/// <summary>
/// Records native progress and applies the requesting client's cancellation or failed-output behavior.
/// </summary>
/// <param name="cancellation">The request's client-owned cancellation source.</param>
/// <param name="checkpoint">The native traversal count at which to cancel, or zero to observe.</param>
/// <param name="failureMode">The selected receiver failure scenario, or an unrelated probe mode.</param>
internal sealed class StackProgressRecorder(CancellationTokenSource cancellation, int checkpoint, string failureMode)
    : IProgress<DebugStackWalkProgress>
{
    /// <summary>
    /// Gets snapshots written on the actor and read after request completion.
    /// </summary>
    internal List<DebugStackWalkProgress> Updates { get; } = [];

    /// <inheritdoc />
    public void Report(DebugStackWalkProgress value)
    {
        Updates.Add(value);
        if (value.State == DebugStackWalkState.Walking && value.InspectedFrames == checkpoint)
        {
            cancellation.Cancel();
        }

        if (failureMode == "cancel-completed" && value.State == DebugStackWalkState.Completed)
        {
            cancellation.Cancel();
        }

        bool failWalking = failureMode == "fail-walking" && value.State == DebugStackWalkState.Walking;
        bool failCompleted = failureMode == "fail-completed" && value.State == DebugStackWalkState.Completed;
        bool failFailed = failureMode == "fail-failed" && value.State == DebugStackWalkState.Failed;
        if (failWalking || failCompleted || failFailed)
        {
            throw new IOException("The progress output destination is closed.");
        }

        if (failureMode == "fail-canceled" && value.State == DebugStackWalkState.Walking)
        {
            throw new OperationCanceledException("The progress destination was canceled independently.");
        }
    }
}
