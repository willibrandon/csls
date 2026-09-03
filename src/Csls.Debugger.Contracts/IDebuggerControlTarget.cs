namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines the engine operations exposed through private debugger control RPC.
/// </summary>
public interface IDebuggerControlTarget
{
    /// <summary>
    /// Gets the current debugger session snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The current snapshot.</returns>
    Task<DebugSessionSnapshot> GetSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Launches a managed target.
    /// </summary>
    /// <param name="request">The concrete target launch.</param>
    /// <param name="cancellationToken">Cancels runtime activation.</param>
    /// <returns>The post-launch snapshot.</returns>
    Task<DebugSessionSnapshot> LaunchAsync(
        DebugLaunchRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attaches to a running managed target.
    /// </summary>
    /// <param name="request">The selected process.</param>
    /// <param name="cancellationToken">Cancels runtime attachment.</param>
    /// <returns>The post-attachment snapshot.</returns>
    Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces source breakpoints for one document.
    /// </summary>
    /// <param name="request">The complete replacement set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The current ordered breakpoint states.</returns>
    Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        DebugSourceBreakpointSetRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces every managed function breakpoint.
    /// </summary>
    /// <param name="request">The complete replacement set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The current ordered breakpoint states.</returns>
    Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        DebugFunctionBreakpointSetRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pauses the target.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The stopped snapshot.</returns>
    Task<DebugSessionSnapshot> PauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Continues the target.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The running snapshot.</returns>
    Task<DebugSessionSnapshot> ContinueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Steps one managed thread.
    /// </summary>
    /// <param name="request">The selected thread and step kind.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The running snapshot.</returns>
    Task<DebugSessionSnapshot> StepAsync(
        DebugStepRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets current managed threads.
    /// </summary>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The bounded current thread snapshot.</returns>
    Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a current-generation managed stack page.
    /// </summary>
    /// <param name="request">The selected thread and page.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The requested stack page.</returns>
    Task<DebugStackTrace> GetStackAsync(
        DebugStackRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets current-generation scopes for a frame.
    /// </summary>
    /// <param name="request">The selected frame.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The current frame scopes.</returns>
    Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        DebugScopesRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a current-generation variable page.
    /// </summary>
    /// <param name="request">The selected container and page.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The requested variable page.</returns>
    Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        DebugVariablesRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets source content by its session-local reference.
    /// </summary>
    /// <param name="request">The selected source reference.</param>
    /// <param name="cancellationToken">Cancels source retrieval.</param>
    /// <returns>The complete source text and media type.</returns>
    Task<DebugSourceContent> GetSourceContentAsync(
        DebugSourceRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the owned target.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for termination.</param>
    /// <returns>The terminal snapshot.</returns>
    Task<DebugSessionSnapshot> TerminateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detaches without terminating the target.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for detachment.</param>
    /// <returns>The terminal snapshot.</returns>
    Task<DebugSessionSnapshot> DetachAsync(CancellationToken cancellationToken);
}
