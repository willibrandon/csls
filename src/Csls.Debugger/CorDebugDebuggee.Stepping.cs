using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one active CoreCLR source step and its Just My Code policy.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
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

            StartRuntimeStep(thread, kind, target);
            ClearFrameHandles();
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
                "ICorDebugController.Continue");
        }
        catch
        {
            CancelStep();
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
    /// <param name="stepper">The borrowed callback ICorDebugStepper pointer.</param>
    /// <returns>True when the callback belongs to this debuggee's active step.</returns>
    internal bool CompleteStep(nint stepper)
    {
        ArgumentOutOfRangeException.ThrowIfZero(stepper);
        nint identity = ComAbi.GetIdentity(stepper);
        try
        {
            if (identity != _activeStepperIdentity)
            {
                return false;
            }

            ReleaseActiveStepper(deactivate: false);
            ReleaseTargetBreakpoint();
            if (_asyncStep is not null)
            {
                return false;
            }

            ReleaseAsyncStep();
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
    internal void CancelStep()
    {
        ReleaseAsyncStep();
        ReleaseTargetBreakpoint();
        ReleaseActiveStepper(deactivate: true);
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
            CorDebugHResult.ThrowIfFailed(stepResult, $"ICorDebugStepper.Step{kind}");
            _activeStepperIdentity = ComAbi.GetIdentity(stepper);
            _activeStepper = stepper;
            stepper = 0;
        }
        finally
        {
            ReleaseUnusedStepper(stepper);
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
