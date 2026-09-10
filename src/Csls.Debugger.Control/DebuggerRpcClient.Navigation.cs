using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes source-aware execution navigation over private debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Gets selectable managed calls in a current source statement.
    /// </summary>
    /// <param name="request">The selected generation-bound active frame.</param>
    /// <param name="cancellationToken">Cancels target discovery.</param>
    /// <returns>The ordered source-aware Step Into targets.</returns>
    public Task<IReadOnlyList<DebugStepTargetInfo>> GetStepTargetsAsync(
        DebugStepTargetsRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugStepTargetsRequest, IReadOnlyList<DebugStepTargetInfo>>(
            DebuggerControlMethods.GetStepTargets,
            request,
            cancellationToken);

    /// <summary>
    /// Gets CoreCLR-approved instruction-pointer destinations.
    /// </summary>
    /// <param name="request">The selected active frame and source position.</param>
    /// <param name="cancellationToken">Cancels destination discovery.</param>
    /// <returns>The ordered safe destinations.</returns>
    public Task<IReadOnlyList<DebugGotoTargetInfo>> GetGotoTargetsAsync(
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugGotoTargetsRequest, IReadOnlyList<DebugGotoTargetInfo>>(
            DebuggerControlMethods.GetGotoTargets,
            request,
            cancellationToken);

    /// <summary>
    /// Moves a managed thread to a previously approved source destination.
    /// </summary>
    /// <param name="request">The selected thread and generation-bound target.</param>
    /// <param name="cancellationToken">Cancels queueing the move.</param>
    /// <returns>The new stopped-generation snapshot.</returns>
    public Task<DebugSessionSnapshot> GotoAsync(
        DebugGotoRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugGotoRequest, DebugSessionSnapshot>(
            DebuggerControlMethods.Goto,
            request,
            cancellationToken);
}
