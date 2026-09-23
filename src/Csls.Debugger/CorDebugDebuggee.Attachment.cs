using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Activates launched and attached CoreCLR debugger targets.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Attaches to an existing process after its CoreCLR runtime has started.
    /// </summary>
    /// <param name="processId">The operating-system process identifier.</param>
    /// <param name="actor">The session actor that owns runtime calls and callbacks.</param>
    /// <param name="observer">Receives debugger diagnostics through the session output channel.</param>
    /// <param name="sourceBreakpoints">The session source-breakpoint owner.</param>
    /// <param name="functionBreakpoints">The session function-breakpoint owner.</param>
    /// <param name="instructionBreakpoints">The session managed-IL breakpoint owner.</param>
    /// <param name="entryBreakpoint">The session one-shot entry breakpoint owner.</param>
    /// <param name="breakpointReached">The ordered runtime-breakpoint decision callback.</param>
    /// <param name="targetBreakpointReached">The ordered targeted-step breakpoint callback.</param>
    /// <param name="stepCompleted">The ordered runtime-step completion callback.</param>
    /// <param name="breakRequested">The ordered explicit managed-break callback.</param>
    /// <param name="exceptionRaised">The ordered managed-exception callback.</param>
    /// <param name="evaluationCompleted">The ordered function-evaluation completion callback.</param>
    /// <param name="cancellationToken">Cancels runtime activation without terminating the target.</param>
    /// <returns>The live debugger attachment.</returns>
    internal static async Task<CorDebugDebuggee> AttachAsync(
        int processId,
        DebuggerSessionActor actor,
        IDebuggerSessionObserver observer,
        SourceBreakpointManager sourceBreakpoints,
        FunctionBreakpointManager functionBreakpoints,
        InstructionBreakpointManager instructionBreakpoints,
        EntryPointBreakpointManager entryBreakpoint,
        Func<int, ManagedBreakpointHit, CancellationToken, ValueTask<bool>> breakpointReached,
        Func<int, nint, CancellationToken, ValueTask<ManagedTargetBreakpointDecision>>
            targetBreakpointReached,
        Func<int, nint, int, CancellationToken, ValueTask<bool>> stepCompleted,
        Func<int, CancellationToken, ValueTask<bool>> breakRequested,
        Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>> exceptionRaised,
        Func<nint, bool, CancellationToken, ValueTask<bool>> evaluationCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(sourceBreakpoints);
        ArgumentNullException.ThrowIfNull(functionBreakpoints);
        ArgumentNullException.ThrowIfNull(instructionBreakpoints);
        ArgumentNullException.ThrowIfNull(entryBreakpoint);
        ArgumentNullException.ThrowIfNull(breakpointReached);
        ArgumentNullException.ThrowIfNull(targetBreakpointReached);
        ArgumentNullException.ThrowIfNull(stepCompleted);
        ArgumentNullException.ThrowIfNull(breakRequested);
        ArgumentNullException.ThrowIfNull(exceptionRaised);
        ArgumentNullException.ThrowIfNull(evaluationCompleted);
        if (processId == Environment.ProcessId)
        {
            throw new ArgumentException("The debugger cannot attach to its own process.", nameof(processId));
        }

        DbgShimLibrary.VerifyPlatformSupport();
        using CorDebugRuntimeActivationLease activationLease = await CorDebugRuntimeActivationLease
            .AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var processOwner = new DisposableOwner<Process>();
        using var managedCallbackOwner = new DisposableOwner<CorDebugManagedCallback>();
        nint corDebug = 0;
        nint debugProcess = 0;
        try
        {
            processOwner.Acquire(() => Process.GetProcessById(processId));
            Process process = processOwner.Value
                ?? throw new InvalidOperationException("The attached process was not acquired.");
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Process {processId} has already exited.");
            }

            string runtimePath = DbgShimRuntimeDiscovery.GetSingleRuntimePath(checked((uint)processId));
            managedCallbackOwner.Acquire(() =>
                new CorDebugManagedCallback(
                actor,
                observer,
                sourceBreakpoints,
                functionBreakpoints,
                instructionBreakpoints,
                entryBreakpoint,
                breakpointReached,
                targetBreakpointReached,
                stepCompleted,
                breakRequested,
                exceptionRaised,
                evaluationCompleted));
            CorDebugManagedCallback managedCallback = managedCallbackOwner.Value
                ?? throw new InvalidOperationException("The managed callback was not created.");
            await actor.InvokeAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    nint debugger = DbgShimRuntimeDiscovery.CreateDebuggingInterface(
                        checked((uint)processId), runtimePath);
                    CorDebugActivationResult activation = CorDebugRuntimeActivation.Attach(
                        debugger, checked((uint)processId), managedCallback, sourceBreakpoints);
                    corDebug = activation.CorDebug;
                    debugProcess = activation.Process;
                    return ValueTask.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
            await managedCallback.WaitForInitializationAsync(cancellationToken)
                .ConfigureAwait(false);
            managedCallback.ThrowIfRuntimeFailed();

            var result = new CorDebugDebuggee(
                actor,
                sourceBreakpoints,
                functionBreakpoints,
                instructionBreakpoints,
                entryBreakpoint,
                managedCallbackOwner,
                registrationOwner: null,
                standardStreamsOwner: null,
                processOwner,
                unixExitMonitor: null,
                ownsProcess: false,
                terminateChildProcesses: false,
                ownsRuntimeLease: true,
                new CorDebugActivationResult(corDebug, debugProcess));
            activationLease.Transfer();
            corDebug = 0;
            debugProcess = 0;
            return result;
        }
        finally
        {
            await DetachRuntimeAsync(actor, corDebug, debugProcess, managedCallbackOwner.Value)
                .ConfigureAwait(false);
        }
    }
}
