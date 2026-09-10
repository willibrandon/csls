using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one active CoreCLR source step and its Just My Code policy.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private readonly ManagedAsyncCallerStep _asyncCallerStep = new();
    private readonly ManagedStepTrace? _stepTrace = ManagedStepTrace.Current;

    /// <summary>
    /// Starts one source-level step on a managed thread and resumes the target.
    /// </summary>
    /// <param name="threadId">The managed thread identifier to step.</param>
    /// <param name="kind">The requested source-level stepping operation.</param>
    /// <param name="targetId">The optional generation-bound Step Into target.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    internal void Step(
        int threadId,
        DebugStepKind kind,
        int? targetId,
        DebugStopGeneration generation)
    {
        _managedCallback.ThrowIfRuntimeFailed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        _stepTrace?.Write($"request thread={threadId} kind={kind} generation={generation.Value}");
        if (_activeStepper != 0)
        {
            throw new InvalidOperationException("A managed step is already active.");
        }

        ManagedStepTargetHandle? target = GetStepTarget(
            threadId,
            kind,
            targetId,
            generation);
        nint thread = 0;
        try
        {
            thread = GetThread(threadId);
            bool targetsCall = target is not null;
            if (targetsCall)
            {
                CreateTargetBreakpoint(target!);
            }

            if (!targetsCall && kind != DebugStepKind.Out)
            {
                PrepareAsyncStep(threadId, thread, kind);
            }

            if (!targetsCall)
            {
                nint stateMachine = GetFirstArgument(thread);
                try
                {
                    if (stateMachine != 0)
                    {
                        _asyncConsumerStep.Prepare(stateMachine, includeTasks: kind == DebugStepKind.Out);
                    }
                }
                finally
                {
                    ReleaseCom(stateMachine);
                }

                if (_asyncStep is null && !_asyncConsumerStep.IsActive)
                {
                    _asyncCallerStep.Prepare(thread, _sourceBreakpoints.FindModule, ConfigureStepper);
                }
            }

            if (kind != DebugStepKind.Out || !_asyncConsumerStep.IsActive)
            {
                StartRuntimeStep(thread, kind, target);
            }

            Continue();
        }
        catch
        {
            CancelStep(runtimeAvailable: RuntimeFailure is null);
            _managedCallback.ThrowIfRuntimeFailed();
            throw;
        }
        finally
        {
            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }
        }
    }

    /// <summary>
    /// Completes the active source step when its runtime callback arrives.
    /// </summary>
    /// <param name="threadId">The completing managed thread.</param>
    /// <param name="stepper">The borrowed callback ICorDebugStepper pointer.</param>
    /// <param name="reason">The runtime step completion reason.</param>
    /// <returns>True when the logical source step has completed at a visible statement.</returns>
    internal bool CompleteStep(int threadId, nint stepper, int reason)
    {
        ArgumentOutOfRangeException.ThrowIfZero(stepper);
        nint identity = ComAbi.GetIdentity(stepper);
        try
        {
            _stepTrace?.Write($"step complete thread={threadId} reason={reason} active={identity == _activeStepperIdentity} caller={_asyncCallerStep.Owns(identity)} await={_asyncStep is not null} resume={_asyncStep?.WaitsForResume}");
            if (_asyncCallerStep.Owns(identity))
            {
                CancelStep();
                nint thread = GetThread(threadId);
                try
                {
                    return ResumeToUserCode(thread) == ManagedTargetBreakpointDecision.Stopped;
                }
                finally
                {
                    ReleaseCom(thread);
                }
            }

            if (identity != _activeStepperIdentity)
            {
                return false;
            }

            ReleaseActiveStepper(deactivate: false);
            ReleaseTargetBreakpoint();
            ReleaseAsyncStep();
            _asyncConsumerStep.Clear();
            // STEP_RETURN can arrive in a runtime wrapper before the authored caller resumes.
            if (reason == 1 && _asyncCallerStep.IsActive)
            {
                return false;
            }

            _asyncCallerStep.Clear();
            return true;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Deactivates and releases an interrupted source step.
    /// </summary>
    /// <param name="runtimeAvailable">Whether the runtime permits breakpoint and handle disposal.</param>
    internal void CancelStep(bool runtimeAvailable = true)
    {
        _stepTrace?.Write($"cancel runtime={runtimeAvailable} active={_activeStepper != 0} await={_asyncStep is not null} resume={_asyncStep?.WaitsForResume}");
        _asyncCallerStep.Clear(runtimeAvailable);
        _asyncConsumerStep.Clear(runtimeAvailable);
        ReleaseAsyncStep(runtimeAvailable);
        ReleaseTargetBreakpoint(runtimeAvailable);
        ReleaseActiveStepper(deactivate: runtimeAvailable);
    }

    private void StartRuntimeStep(
        nint thread,
        DebugStepKind kind,
        ManagedStepTargetHandle? target = null)
    {
        nint stepper = 0;
        try
        {
            stepper = CreateStepper(thread);
            ConfigureStepper(stepper);
            int stepResult = target is null
                ? StartStep(stepper, thread, kind)
                : StartGuardedTargetStep(stepper, target);
            _stepTrace?.Write($"runtime step result kind={kind} result=0x{stepResult:X8}");
            CorDebugHResult.ThrowIfFailed(stepResult, $"ICorDebugStepper.Step{kind}");
            _activeStepperIdentity = ComAbi.GetIdentity(stepper);
            _activeStepper = stepper;
            stepper = 0;
        }
        finally
        {
            ReleaseUnusedStepper(stepper, runtimeAvailable: RuntimeFailure is null);
        }
    }

    private ManagedStepTargetHandle? GetStepTarget(
        int threadId,
        DebugStepKind kind,
        int? targetId,
        DebugStopGeneration generation)
    {
        if (targetId is null)
        {
            return null;
        }

        if (kind != DebugStepKind.Into)
        {
            throw new ArgumentException("Only Step Into accepts a target identifier.");
        }

        if (!_stepTargets.TryGetValue(targetId.Value, out ManagedStepTargetHandle? target) ||
            target.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Step Into target {targetId.Value} is stale or unknown.");
        }

        if (target.ThreadId != threadId)
        {
            throw new InvalidOperationException(
                $"Step Into target {targetId.Value} belongs to managed thread {target.ThreadId}.");
        }

        return target;
    }

}
