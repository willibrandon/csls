using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Applies Hot Reload updates and publishes stopped-state invalidation.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<DebugHotReloadResult> ApplyHotReloadAsync(
        DebugHotReloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugHotReloadResult result = await _session.ApplyHotReloadAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = DebugSessionState.Stopped,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopReason = "hot reload",
            StoppedThreadId = current.StoppedThreadId,
            StopGeneration = result.StopGeneration,
            Exception = current.Exception
        });
        NotifyResourceChanged(
            DebuggerResourceChangeKind.Breakpoints |
            DebuggerResourceChangeKind.Variables);
        return result;
    }
}
