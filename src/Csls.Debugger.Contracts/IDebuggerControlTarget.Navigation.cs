namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines source-aware execution navigation exposed through private debugger control RPC.
/// </summary>
public partial interface IDebuggerControlTarget
{
    /// <summary>
    /// Gets selectable managed calls in a current source statement.
    /// </summary>
    /// <param name="request">The selected generation-bound active frame.</param>
    /// <param name="cancellationToken">Cancels target discovery.</param>
    /// <returns>The ordered source-aware Step Into targets.</returns>
    Task<IReadOnlyList<DebugStepTargetInfo>> GetStepTargetsAsync(
        DebugStepTargetsRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets CoreCLR-approved instruction-pointer destinations.
    /// </summary>
    /// <param name="request">The selected active frame and source position.</param>
    /// <param name="cancellationToken">Cancels destination discovery.</param>
    /// <returns>The ordered safe destinations.</returns>
    Task<IReadOnlyList<DebugGotoTargetInfo>> GetGotoTargetsAsync(
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a managed thread to a previously approved source destination.
    /// </summary>
    /// <param name="request">The selected thread and generation-bound target.</param>
    /// <param name="cancellationToken">Cancels queueing the move.</param>
    /// <returns>The new stopped-generation snapshot.</returns>
    Task<DebugSessionSnapshot> GotoAsync(
        DebugGotoRequest request,
        CancellationToken cancellationToken);
}
