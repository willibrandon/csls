namespace Csls.Debugger.Contracts;

/// <summary>
/// Provides bounded thread, stack, scope, variable, and module inspection for live and offline targets.
/// </summary>
public interface IDebuggerInspectionTarget
{
    /// <summary>
    /// Gets the current managed-thread snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancels thread enumeration.</param>
    /// <returns>The bounded managed-thread snapshot.</returns>
    Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of managed stack frames.
    /// </summary>
    /// <param name="request">The selected thread and frame page.</param>
    /// <param name="cancellationToken">Cancels stack enumeration.</param>
    /// <returns>The frame page and exact total when available.</returns>
    Task<DebugStackTrace> GetStackAsync(DebugStackRequest request, CancellationToken cancellationToken);

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
    /// Gets a current-generation variable page with explicit target-execution authorization.
    /// </summary>
    /// <param name="request">The selected container, page, and execution policy.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The requested variable page.</returns>
    Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        DebugVariablesRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of managed modules.
    /// </summary>
    /// <param name="request">The selected module page.</param>
    /// <param name="cancellationToken">Cancels module enumeration.</param>
    /// <returns>The module page and complete module count.</returns>
    Task<DebugModulePage> GetModulesAsync(DebugModulesRequest request, CancellationToken cancellationToken);
}
