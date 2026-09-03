using Csls.Debugger.Contracts;
using Csls.Debugger.Evaluation;

namespace Csls.Debugger;

/// <summary>
/// Owns one protocol-neutral debugger target and its ordered lifecycle.
/// </summary>
public sealed partial class DebuggerSession : IAsyncDisposable
{
    private readonly IDebuggerSessionObserver _observer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSessionActor _actor = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly DebuggerEvaluatorSupervisor _evaluator = new();
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private readonly FunctionBreakpointManager _functionBreakpoints;
    private readonly InstructionBreakpointManager _instructionBreakpoints;
    private IDebuggeeProcess? _debuggee;
    private Task? _debuggeeLifetime;
    private CancellationTokenSource? _debuggeeObservationCancellation;
    private DebugStopGeneration _stopGeneration;
    private volatile DebugSessionState _state = DebugSessionState.Created;
    private int _disposed;

    /// <summary>
    /// Creates a debugger session connected to one protocol or control-plane observer.
    /// </summary>
    /// <param name="observer">The ordered target notification observer.</param>
    internal DebuggerSession(IDebuggerSessionObserver observer)
    {
        _observer = observer;
        _sourceBreakpoints = new SourceBreakpointManager(
            (breakpoint, cancellationToken) =>
                _observer.OnBreakpointChangedAsync(breakpoint, cancellationToken));
        _functionBreakpoints = new FunctionBreakpointManager(
            (breakpoint, cancellationToken) =>
                _observer.OnFunctionBreakpointChangedAsync(breakpoint, cancellationToken));
        _instructionBreakpoints = new InstructionBreakpointManager(
            (breakpoint, cancellationToken) =>
                _observer.OnInstructionBreakpointChangedAsync(breakpoint, cancellationToken));
    }

    /// <summary>
    /// Gets the current protocol-neutral debugger state.
    /// </summary>
    public DebugSessionState State => _state;

    /// <summary>
    /// Gets the current stopped-state generation, or zero before the first debugger stop.
    /// </summary>
    public DebugStopGeneration StopGeneration => _stopGeneration;

}
