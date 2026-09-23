using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Separates read-only expression inspection from authorized target execution.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugEvaluateResult> EvaluateAsync(
        DebugEvaluateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.EvaluateAsync(
            request.FrameId,
            request.Expression,
            allowTargetCodeExecution: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DebugEvaluateResult> ExecuteExpressionAsync(
        DebugExecuteExpressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugStopGeneration initialGeneration = _session.StopGeneration;
        try
        {
            return await _session.EvaluateAsync(
                request.FrameId,
                request.Expression,
                allowTargetCodeExecution: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SynchronizeEvaluationState(initialGeneration);
        }
    }

    private void SynchronizeEvaluationState(DebugStopGeneration initialGeneration)
    {
        DebugStopGeneration generation = _session.StopGeneration;
        DebugSessionState state = _session.State;
        if (generation == initialGeneration ||
            state is not (DebugSessionState.Stopped or DebugSessionState.Faulted))
        {
            return;
        }

        DebugSessionSnapshot current = GetSnapshot();
        UpdateSnapshot(new DebugSessionSnapshot
        {
            State = state,
            ProcessName = current.ProcessName,
            ProcessId = current.ProcessId,
            StopReason = current.StopReason,
            StoppedThreadId = current.StoppedThreadId,
            StopGeneration = generation.Value,
            Exception = current.Exception,
            ExitCode = current.ExitCode
        });
    }
}
