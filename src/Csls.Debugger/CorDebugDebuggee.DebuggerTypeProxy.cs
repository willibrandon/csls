using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Coordinates target-code construction and retention for debugger type proxies.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Starts proxy construction for one default-view value when target metadata requests it.
    /// </summary>
    /// <param name="variablesReference">The current generation-owned value reference.</param>
    /// <param name="generation">The current stop generation.</param>
    /// <param name="completion">Receives the proxy evaluation completion.</param>
    /// <returns>True when proxy construction was scheduled.</returns>
    internal bool TryBeginDebuggerTypeProxyEvaluation(
        int variablesReference,
        DebugStopGeneration generation,
        out Task<ManagedFunctionEvaluationResult>? completion)
    {
        completion = null;
        if (_activeFunctionEvaluation is not null ||
            _functionEvaluationDisabledReason is not null ||
            !_values.TryGetValue(variablesReference, out ManagedValueHandle? handle) ||
            handle.View != ManagedValueView.Default ||
            handle.ThreadId is not int threadId)
        {
            return false;
        }

        ValidateGeneration(variablesReference, handle.Generation, generation);
        nint inspectedValue = 0;
        nint thread = 0;
        ManagedDebuggerTypeProxyBinding? binding = null;
        try
        {
            thread = GetThread(threadId);
            if (!TryDereferenceAndUnboxValue(handle.Pointer, out inspectedValue) ||
                !_debuggerTypeProxyResolver.TryResolve(inspectedValue, thread, out binding))
            {
                return false;
            }
        }
        finally
        {
            if (inspectedValue != 0)
            {
                _ = ComAbi.Release(inspectedValue);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }
        }

        try
        {
            completion = BeginDebuggerTypeProxyEvaluationAsync(handle, threadId, binding!);
            return true;
        }
        catch (Exception exception) when (
            _functionEvaluationDisabledReason is null &&
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException)
        {
            completion = null;
            return false;
        }
    }

    private Task<ManagedFunctionEvaluationResult> BeginDebuggerTypeProxyEvaluationAsync(
        ManagedValueHandle handle,
        int threadId,
        ManagedDebuggerTypeProxyBinding binding)
    {
        nint function = binding.Function;
        nint[] typeArguments = binding.TypeArguments;
        nint thread = 0;
        nint evaluation = 0;
        nint targetHandle = 0;
        bool resourcesTransferred = false;
        bool callbackEvaluationActive = false;
        bool callScheduled = false;
        try
        {
            thread = GetThread(threadId);
            evaluation = CreateEvaluation(thread);
            targetHandle = CreateFunctionEvaluationHandle(handle.Pointer);
            ManagedValueDisplay display = FormatRuntimeValue(handle.Pointer);
            ManagedExpressionValue[] arguments =
            [
                new ManagedExpressionValue(
                    new DebugVariableInfo(
                        "$proxyTarget",
                        display.Value,
                        display.Type,
                        handle.Id,
                        handle.MemoryReference,
                        handle.EvaluateName),
                    Scalar: null,
                    HasScalar: false)
            ];
            var threadStates = new Dictionary<int, int>();
            var active = new ManagedFunctionEvaluation
            {
                Pointer = evaluation,
                Function = function,
                TypeArguments = typeArguments,
                Thread = thread,
                Receiver = 0,
                ConstructsObject = true,
                MaterializesString = false,
                Arguments = arguments,
                RuntimeArguments = [targetHandle],
                ThreadId = threadId,
                ThreadStates = threadStates,
                DebuggerTypeProxy = new ManagedDebuggerTypeProxyEvaluation(
                    handle.EvaluateName,
                    threadId)
            };
            _activeFunctionEvaluation = active;
            resourcesTransferred = true;
            evaluation = 0;
            function = 0;
            thread = 0;
            targetHandle = 0;
            typeArguments = [];
            SuspendOtherThreads(threadId, threadStates);
            _managedCallback.BeginFunctionEvaluation();
            callbackEvaluationActive = true;
            ScheduleNextFunctionEvaluationStage(active);
            callScheduled = true;
            ClearFrameHandles();
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
                "ICorDebugController.Continue");
            return active.Completion.Task.WaitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (callScheduled)
            {
                _functionEvaluationDisabledReason =
                    "The debugger could not resume the target after scheduling a debugger " +
                    "type proxy. The target's evaluation state is uncertain; this debugger " +
                    "session must be disconnected.";
            }

            if (_activeFunctionEvaluation is ManagedFunctionEvaluation active)
            {
                _activeFunctionEvaluation = null;
                try
                {
                    RestoreThreadStates(active);
                }
                finally
                {
                    ReleaseFunctionEvaluationResources(active);
                }
            }

            if (callbackEvaluationActive)
            {
                _managedCallback.EndFunctionEvaluation();
            }

            if (callScheduled)
            {
                throw new InvalidOperationException(
                    _functionEvaluationDisabledReason,
                    exception);
            }

            throw new InvalidOperationException(
                $"Debugger type proxy construction could not start: {exception.Message}",
                exception);
        }
        finally
        {
            if (!resourcesTransferred)
            {
                ReleaseFunctionEvaluationHandle(targetHandle);
                foreach (nint typeArgument in typeArguments)
                {
                    ReleaseFunctionEvaluationPointer(typeArgument);
                }

                ReleaseFunctionEvaluationPointer(function);
                if (evaluation != 0)
                {
                    _ = ComAbi.Release(evaluation);
                }

                if (thread != 0)
                {
                    _ = ComAbi.Release(thread);
                }
            }
        }
    }

    private ManagedFunctionEvaluationResult CreateDebuggerTypeProxyFallback(
        ManagedFunctionEvaluation active,
        DebugStopGeneration generation)
    {
        ManagedValueHandle original = RetainDebuggerTypeProxyOriginal(
            active,
            generation,
            ManagedValueView.ProxyBypassed);
        ManagedValueDisplay display = FormatRuntimeValue(original.Pointer);
        return new ManagedFunctionEvaluationResult(
            new DebugEvaluateResult(
                display.Value,
                display.Type,
                original.Id,
                original.MemoryReference,
                TargetCodeExecuted: true),
            original.Id,
            generation,
            DebuggerTypeProxyApplied: false);
    }

    private ManagedValueHandle RetainDebuggerTypeProxyOriginal(
        ManagedFunctionEvaluation active,
        DebugStopGeneration generation,
        ManagedValueView view)
    {
        ManagedDebuggerTypeProxyEvaluation context = active.DebuggerTypeProxy ??
            throw new InvalidOperationException("No debugger type-proxy evaluation is active.");
        if (active.RuntimeArguments is not [{ } targetHandle])
        {
            throw new InvalidOperationException(
                "The debugger type-proxy evaluation does not own its target handle.");
        }

        nint original = 0;
        try
        {
            if (!TryDereferenceValue(targetHandle, out original))
            {
                throw new InvalidOperationException(
                    "The debugger type-proxy target became null during construction.");
            }

            return RetainRuntimeValue(
                original,
                generation,
                context.EvaluateName,
                frameId: null,
                context.ThreadId,
                view,
                tupleCustomTypeInfo: null);
        }
        finally
        {
            if (original != 0)
            {
                _ = ComAbi.Release(original);
            }
        }
    }
}
