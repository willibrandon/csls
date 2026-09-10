using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes source-aware execution navigation through private control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugStepTargetInfo>> GetStepTargetsAsync(
        DebugStepTargetsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetStepTargetsAsync(request.FrameId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugGotoTargetInfo>> GetGotoTargetsAsync(
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetGotoTargetsAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> GotoAsync(
        DebugGotoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.GotoAsync(request, cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }
}
