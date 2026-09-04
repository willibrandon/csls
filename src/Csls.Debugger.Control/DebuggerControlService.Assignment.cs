using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Applies generation-safe assignments and publishes variable invalidation.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<DebugAssignmentResult> SetVariableAsync(
        DebugSetVariableRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugStopGeneration initialGeneration = _session.StopGeneration;
        DebugAssignmentResult result;
        bool completed = false;
        try
        {
            result = await _session.SetVariableAsync(
                request.VariablesReference,
                request.Name,
                request.Value,
                new DebugStopGeneration(request.StopGeneration),
                cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            bool generationChanged = _session.StopGeneration != initialGeneration;
            SynchronizeEvaluationState(initialGeneration);
            if (completed || generationChanged)
            {
                NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DebugAssignmentResult> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugStopGeneration initialGeneration = _session.StopGeneration;
        DebugAssignmentResult result;
        bool completed = false;
        try
        {
            result = await _session.SetExpressionAsync(
                request.FrameId,
                request.Expression,
                request.Value,
                new DebugStopGeneration(request.StopGeneration),
                cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            bool generationChanged = _session.StopGeneration != initialGeneration;
            SynchronizeEvaluationState(initialGeneration);
            if (completed || generationChanged)
            {
                NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
            }
        }

        return result;
    }
}
