using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-bound managed debugger inspection operations.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces every source breakpoint requested for one absolute document path.
    /// </summary>
    /// <param name="sourcePath">The absolute source document path.</param>
    /// <param name="breakpoints">The complete replacement breakpoint list.</param>
    /// <param name="cancellationToken">Cancels queueing or runtime binding.</param>
    /// <returns>The ordered current breakpoint binding states.</returns>
    public async Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        string sourcePath,
        IReadOnlyList<DebugSourceBreakpointRequest> breakpoints,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugSourceBreakpointInfo>? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                if (_state is not DebugSessionState.Created and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Source breakpoints cannot be changed while the debugger session is {_state}.");
                }

                result = await _sourceBreakpoints
                    .SetAsync(sourcePath, breakpoints, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets the managed threads belonging to the current stop generation.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing thread enumeration.</param>
    /// <returns>The bounded current managed-thread snapshot.</returns>
    public async Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugThreadInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed threads are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetThreads();
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets a page of managed modules observed in the active target.
    /// </summary>
    /// <param name="startModule">The zero-based first module to return.</param>
    /// <param name="moduleCount">The maximum count, or zero for all remaining modules.</param>
    /// <param name="cancellationToken">Cancels queueing module inspection.</param>
    /// <returns>The selected module page and complete module count.</returns>
    public async Task<DebugModulePage> GetModulesAsync(
        int startModule,
        int moduleCount,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugModulePage? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state is not DebugSessionState.Running and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Managed modules are unavailable while the debugger session is {_state}.");
                }

                result = new DebugModulePage(
                    _sourceBreakpoints.GetModules(startModule, moduleCount),
                    _sourceBreakpoints.ModuleCount);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets a page of managed stack frames belonging to the current stop generation.
    /// </summary>
    /// <param name="threadId">The managed thread identifier.</param>
    /// <param name="startFrame">The zero-based first frame to return.</param>
    /// <param name="levels">The maximum count, or zero for all remaining frames.</param>
    /// <param name="cancellationToken">Cancels queueing and native stack enumeration.</param>
    /// <param name="progress">Receives bounded progress synchronously on the debugger actor.</param>
    /// <returns>The selected frame page and exact stack count when the end has been observed.</returns>
    public async Task<DebugStackTrace> GetStackTraceAsync(
        int threadId,
        int startFrame,
        int levels,
        CancellationToken cancellationToken,
        IProgress<DebugStackWalkProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugStackTrace? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed stack frames are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetStackTrace(
                    threadId,
                    _stopGeneration,
                    startFrame,
                    levels,
                    token,
                    progress);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets runtime-backed scopes for a frame in the current stop generation.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier for the visible stop.</param>
    /// <param name="cancellationToken">Cancels queueing scope creation and physical frame reacquisition.</param>
    /// <returns>The frame's available variable scopes.</returns>
    public async Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        int frameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugScopeInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed scopes are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetScopes(frameId, _stopGeneration, token);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets one page of immediate variables from a current-generation scope.
    /// </summary>
    /// <param name="variablesReference">The generation-bound variable-container handle.</param>
    /// <param name="start">The zero-based first variable to return.</param>
    /// <param name="count">The maximum count, or zero for all remaining values.</param>
    /// <param name="allowTargetCodeExecution">Whether target-code presentation is authorized.</param>
    /// <param name="cancellationToken">Cancels queueing variable enumeration.</param>
    /// <param name="filter">The child category to select before applying pagination.</param>
    /// <returns>The requested immediate variable page.</returns>
    public async Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        int variablesReference,
        int start,
        int count,
        bool allowTargetCodeExecution,
        CancellationToken cancellationToken,
        DebugVariableFilter filter = DebugVariableFilter.All)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        IReadOnlyList<DebugVariableInfo>? result = null;
        Task<ManagedFunctionEvaluationResult>? proxyEvaluation = null;
        bool resultsViewEvaluation = false;
        CorDebugDebuggee? evaluationDebuggee = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed variables are unavailable while the debugger session is {_state}.");
                }

                if (allowTargetCodeExecution &&
                    (managedDebuggee.TryBeginDebuggerTypeProxyEvaluation(
                        variablesReference,
                        _stopGeneration,
                        out proxyEvaluation) ||
                    (resultsViewEvaluation = managedDebuggee.TryBeginResultsViewEvaluation(
                        variablesReference,
                        _stopGeneration,
                        out proxyEvaluation))))
                {
                    evaluationDebuggee = managedDebuggee;
                    _state = DebugSessionState.Running;
                }
                else
                {
                    result = managedDebuggee.GetVariables(
                        variablesReference,
                        _stopGeneration,
                        start,
                        count,
                        filter);
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        if (proxyEvaluation is null)
        {
            return result!;
        }

        ManagedFunctionEvaluationResult proxy = await WaitForFunctionEvaluationAsync(
            evaluationDebuggee!,
            proxyEvaluation,
            cancellationToken).ConfigureAwait(false);
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee managedDebuggee = GetStoppedManagedDebuggee();
                if (proxy.Generation != _stopGeneration)
                {
                    throw new InvalidOperationException(
                        "The debugger presentation belongs to a retired stop generation.");
                }

                result = resultsViewEvaluation
                    ? [managedDebuggee.GetResultsViewSnapshot(proxy.RuntimeValueReference, proxy.Generation)]
                    : managedDebuggee.GetVariables(
                        proxy.RuntimeValueReference,
                        proxy.Generation,
                        start,
                        count,
                        filter);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
