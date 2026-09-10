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
        long initialMutationRevision = _session.VariableMutationRevision;
        try
        {
            return await _session.SetVariableAsync(
                request.VariablesReference,
                request.Name,
                request.Value,
                new DebugStopGeneration(request.StopGeneration),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool generationChanged = _session.StopGeneration != initialGeneration;
            SynchronizeEvaluationState(initialGeneration);
            if (_session.VariableMutationRevision != initialMutationRevision || generationChanged)
            {
                NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
            }
        }
    }

    /// <inheritdoc />
    public async Task<DebugAssignmentResult> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugStopGeneration initialGeneration = _session.StopGeneration;
        long initialMutationRevision = _session.VariableMutationRevision;
        try
        {
            return await _session.SetExpressionAsync(
                request.FrameId,
                request.Expression,
                request.Value,
                new DebugStopGeneration(request.StopGeneration),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool generationChanged = _session.StopGeneration != initialGeneration;
            SynchronizeEvaluationState(initialGeneration);
            if (_session.VariableMutationRevision != initialMutationRevision || generationChanged)
            {
                NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
            }
        }
    }
}
