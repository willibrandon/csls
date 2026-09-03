using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Applies generation-safe assignments and publishes variable invalidation.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<DebugVariableInfo> SetVariableAsync(
        DebugSetVariableRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugVariableInfo result = await _session.SetVariableAsync(
            request.VariablesReference,
            request.Name,
            request.Value,
            new DebugStopGeneration(request.StopGeneration),
            cancellationToken).ConfigureAwait(false);
        NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
        return result;
    }

    /// <inheritdoc />
    public async Task<DebugVariableInfo> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DebugVariableInfo result = await _session.SetExpressionAsync(
            request.FrameId,
            request.Expression,
            request.Value,
            new DebugStopGeneration(request.StopGeneration),
            cancellationToken).ConfigureAwait(false);
        NotifyResourceChanged(DebuggerResourceChangeKind.Variables);
        return result;
    }
}
