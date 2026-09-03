using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Executes explicitly authorized managed calls through CoreCLR function evaluation.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumFunctionEvaluationArgumentCount = 64;
    private const int IllegalInStackOverflowHResult = unchecked((int)0x80131C22);
    private const int IllegalAtGcUnsafePointHResult = unchecked((int)0x80131C23);
    private const int IllegalInPrologHResult = unchecked((int)0x80131C24);
    private const int IllegalInNativeCodeHResult = unchecked((int)0x80131C25);
    private const int IllegalInOptimizedCodeHResult = unchecked((int)0x80131C26);

    /// <summary>
    /// Gets whether target code is currently executing for a debugger evaluation.
    /// </summary>
    internal bool IsFunctionEvaluationActive => _activeFunctionEvaluation is not null;

    /// <summary>
    /// Gets the safety failure that requires the debugger session to end, if any.
    /// </summary>
    internal string? FunctionEvaluationSafetyFailure => _functionEvaluationDisabledReason;

    /// <summary>
    /// Gets the active evaluation completion used by deterministic session shutdown.
    /// </summary>
    internal Task<DebugEvaluateResult>? ActiveFunctionEvaluationCompletion =>
        _activeFunctionEvaluation?.Completion.Task;

    /// <summary>
    /// Starts one managed-method evaluation and resumes only its selected managed thread.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="plan">The validated invocation expression.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>The result completed by the matching CoreCLR evaluation callback.</returns>
    internal Task<DebugEvaluateResult> BeginFunctionEvaluationAsync(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation)
    {
        if (_activeFunctionEvaluation is not null)
        {
            throw new InvalidOperationException(
                "Only one managed function evaluation may run at a time.");
        }

        if (_functionEvaluationDisabledReason is not null)
        {
            throw new InvalidOperationException(_functionEvaluationDisabledReason);
        }

        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(plan, frame.ExpressionLanguage);
        DebugExpressionNode operation = plan.Root;
        if (operation.Kind is not DebugExpressionNodeKind.Invocation and
            not DebugExpressionNodeKind.ObjectCreation)
        {
            throw new InvalidDataException(
                "Target-code evaluation requires an invocation or object-creation root.");
        }

        bool constructsObject = operation.Kind == DebugExpressionNodeKind.ObjectCreation;
        int argumentOffset = constructsObject ? 0 : 1;
        int argumentCount = operation.Children.Count - argumentOffset;
        if (argumentCount > MaximumFunctionEvaluationArgumentCount)
        {
            throw new NotSupportedException(
                $"Managed function evaluation supports at most " +
                $"{MaximumFunctionEvaluationArgumentCount} method arguments.");
        }

        ManagedExpressionValue? receiver = null;
        var suppliedArguments = new ManagedExpressionValue[argumentCount];
        try
        {
            try
            {
                if (!constructsObject)
                {
                    receiver = EvaluateNode(
                        frame,
                        plan,
                        operation.Children[0],
                        generation);
                }
            }
            catch (InvalidOperationException) when (TryGetQualifiedTypeName(
                operation.Children[0],
                out _))
            {
            }

            for (int index = 0; index < suppliedArguments.Length; index++)
            {
                suppliedArguments[index] = EvaluateNode(
                    frame,
                    plan,
                    operation.Children[index + argumentOffset],
                    generation);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Managed function evaluation failed while binding its receiver or arguments: " +
                exception.Message,
                exception);
        }

        nint receiverValue = receiver is null ? 0 : GetRuntimeValue(receiver);
        nint dereferencedReceiver = 0;
        nint objectValue = 0;
        nint function = 0;
        nint thread = 0;
        nint evaluation = 0;
        nint receiverHandle = 0;
        nint[] runtimeArguments = new nint[argumentCount];
        bool argumentHandlesTransferred = false;
        bool callbackEvaluationActive = false;
        bool callScheduled = false;
        string setupPhase = "resolving the invocation receiver";
        try
        {
            if (receiverValue != 0)
            {
                dereferencedReceiver = DereferenceValue(receiverValue);
                if (!ComAbi.TryQueryInterface(
                    dereferencedReceiver,
                    ICorDebugObjectValueAbi.InterfaceId,
                    out objectValue))
                {
                    throw new InvalidOperationException(
                        "The invocation receiver is not a managed object value.");
                }
            }

            setupPhase = "resolving the runtime method";
            function = constructsObject
                ? ResolveConstructor(
                    operation.Text!,
                    plan.Language,
                    suppliedArguments)
                : receiverValue == 0
                ? ResolveStaticFunction(
                    operation.Children[0],
                    operation.Text!,
                    plan.Language,
                    suppliedArguments)
                : ResolveInstanceFunction(
                    dereferencedReceiver,
                    operation.Text!,
                    plan.Language,
                    suppliedArguments);
            setupPhase = "creating the CoreCLR evaluation";
            thread = GetThread(frame.ThreadId);
            evaluation = CreateEvaluation(thread);
            if (receiverValue != 0)
            {
                receiverHandle = CreateFunctionEvaluationHandle(receiverValue);
            }
            for (int index = 0; index < suppliedArguments.Length; index++)
            {
                if (suppliedArguments[index].Display.VariablesReference > 0)
                {
                    runtimeArguments[index] = CreateFunctionEvaluationHandle(
                        GetRuntimeValue(suppliedArguments[index]));
                }
            }

            Dictionary<int, int> threadStates = [];
            var active = new ManagedFunctionEvaluation
            {
                Pointer = evaluation,
                Function = function,
                Thread = thread,
                Receiver = receiverHandle,
                ConstructsObject = constructsObject,
                Arguments = suppliedArguments,
                RuntimeArguments = runtimeArguments,
                ThreadId = frame.ThreadId,
                ThreadStates = threadStates
            };
            _activeFunctionEvaluation = active;
            argumentHandlesTransferred = true;
            evaluation = 0;
            function = 0;
            thread = 0;
            SuspendOtherThreads(frame.ThreadId, threadStates);
            _managedCallback.BeginFunctionEvaluation();
            callbackEvaluationActive = true;

            setupPhase = "starting the CoreCLR evaluation";
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
            Exception failure = exception;
            if (callScheduled)
            {
                _functionEvaluationDisabledReason =
                    "The debugger could not resume the target after scheduling managed " +
                    "function evaluation. The target's evaluation state is uncertain; this " +
                    "debugger session must be disconnected.";
            }

            try
            {
                if (_activeFunctionEvaluation is ManagedFunctionEvaluation active)
                {
                    _activeFunctionEvaluation = null;
                    try
                    {
                        RestoreThreadStates(active);
                    }
                    catch (Exception restoreException) when (
                        restoreException is InvalidOperationException or IOException)
                    {
                        _functionEvaluationDisabledReason =
                            "Managed function evaluation is disabled because setup failed and " +
                            "thread state could not be restored safely.";
                        failure = new AggregateException(exception, restoreException);
                    }
                    finally
                    {
                        ReleaseFunctionEvaluationResources(active);
                    }
                }
            }
            finally
            {
                if (callbackEvaluationActive)
                {
                    _managedCallback.EndFunctionEvaluation();
                }
            }

            throw new InvalidOperationException(
                $"Managed function evaluation failed while {setupPhase}: {failure.Message}",
                failure);
        }
        finally
        {
            if (!argumentHandlesTransferred)
            {
                foreach (nint runtimeArgument in runtimeArguments)
                {
                    ReleaseFunctionEvaluationHandle(runtimeArgument);
                }

                ReleaseFunctionEvaluationHandle(receiverHandle);
            }

            if (evaluation != 0)
            {
                _ = ComAbi.Release(evaluation);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }

            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }

            if (objectValue != 0)
            {
                _ = ComAbi.Release(objectValue);
            }

            if (dereferencedReceiver != 0)
            {
                _ = ComAbi.Release(dereferencedReceiver);
            }
        }
    }

    /// <summary>
    /// Completes the matching in-flight evaluation without resuming the target.
    /// </summary>
    /// <param name="evaluation">The callback-owned ICorDebugEval pointer.</param>
    /// <param name="isException">Whether CoreCLR reported an evaluation exception.</param>
    /// <param name="resultGeneration">The new stop generation after target execution.</param>
    /// <returns>True when the callback belongs to the active evaluation.</returns>
    internal unsafe bool CompleteFunctionEvaluation(
        nint evaluation,
        bool isException,
        DebugStopGeneration resultGeneration)
    {
        ManagedFunctionEvaluation? active = _activeFunctionEvaluation;
        if (active is null)
        {
            return false;
        }

        if (evaluation != active.Pointer)
        {
            return false;
        }

        Exception? stageFailure = null;
        if (!active.MethodCallScheduled && !active.AbortRequested && !isException)
        {
            try
            {
                ContinueAfterStringMaterialization(active);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or IOException or
                UnauthorizedAccessException or BadImageFormatException)
            {
                stageFailure = new InvalidOperationException(
                    "Managed function evaluation failed while materializing a string " +
                    $"argument: {exception.Message}",
                    exception);
            }
        }

        _activeFunctionEvaluation = null;
        DebugEvaluateResult? result = null;
        Exception? failure = stageFailure;
        nint value = 0;
        bool handlesCleared = false;
        try
        {
            ClearFrameHandles();
            handlesCleared = true;
            if (failure is null)
            {
                nint* valueAddress = &value;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugEvalAbi(evaluation).GetResult((nint)valueAddress),
                    "ICorDebugEval.GetResult");
                value = Volatile.Read(ref *valueAddress);
                if (active.AbortRequested)
                {
                    failure = new OperationCanceledException(
                        "Managed function evaluation was canceled cooperatively.");
                }
                else if (value == 0)
                {
                    result = new DebugEvaluateResult(
                        string.Empty,
                        "void",
                        VariablesReference: 0,
                        MemoryReference: null,
                        TargetCodeExecuted: true);
                }
                else
                {
                    ManagedValueDisplay display = FormatRuntimeValue(value);
                    if (isException)
                    {
                        failure = new InvalidOperationException(
                            $"Managed function evaluation threw {display.Type}: {display.Value}");
                    }
                    else
                    {
                        ManagedValueReferences references = RetainValue(
                            value,
                            resultGeneration,
                            evaluateName: null,
                            frameId: null);
                        result = new DebugEvaluateResult(
                            display.Value,
                            display.Type,
                            references.VariablesReference,
                            references.MemoryReference,
                            TargetCodeExecuted: true);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or
            OperationCanceledException)
        {
            failure = exception is OperationCanceledException
                ? exception
                : new InvalidOperationException(
                    $"Managed function evaluation failed while reading its result: " +
                    exception.Message,
                    exception);
        }
        finally
        {
            try
            {
                if (value != 0)
                {
                    _ = ComAbi.Release(value);
                }

                if (!handlesCleared)
                {
                    ClearFrameHandles();
                }

                try
                {
                    RestoreThreadStates(active);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or IOException)
                {
                    const string reason =
                        "Managed function evaluation completed, but the debugger could not " +
                        "restore thread state safely. This debugger session must be " +
                        "disconnected.";
                    failure = new InvalidOperationException(reason, exception);
                    _functionEvaluationDisabledReason = reason;
                }
            }
            finally
            {
                _managedCallback.EndFunctionEvaluation();
                ReleaseFunctionEvaluationResources(active);
            }
        }

        if (failure is OperationCanceledException canceled)
        {
            _ = active.Completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (failure is not null)
        {
            _ = active.Completion.TrySetException(failure);
        }
        else
        {
            _ = active.Completion.TrySetResult(result!);
        }

        return true;
    }

    /// <summary>
    /// Requests cooperative cancellation of the active function evaluation.
    /// </summary>
    /// <returns>True when an active evaluation accepted the abort request.</returns>
    internal bool AbortFunctionEvaluation()
    {
        ManagedFunctionEvaluation? active = _activeFunctionEvaluation;
        if (active is null)
        {
            return false;
        }

        if (!active.AbortRequested)
        {
            active.AbortRequested = true;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(active.Pointer).Abort(),
                "ICorDebugEval.Abort");
        }

        return true;
    }

    /// <summary>
    /// Prevents further target-code evaluation after an evaluation failed to settle safely.
    /// </summary>
    /// <param name="reason">The developer-facing safety diagnosis.</param>
    internal void DisableFunctionEvaluation(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _functionEvaluationDisabledReason = reason;
    }

    /// <summary>
    /// Fails and releases an evaluation whose target is being torn down.
    /// </summary>
    /// <param name="exception">The terminal evaluation failure.</param>
    internal void FailFunctionEvaluation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ManagedFunctionEvaluation? active = _activeFunctionEvaluation;
        if (active is null)
        {
            return;
        }

        _activeFunctionEvaluation = null;
        _managedCallback.EndFunctionEvaluation();
        ReleaseFunctionEvaluationResources(active);
        _ = active.Completion.TrySetException(exception);
    }

    private nint GetRuntimeValue(ManagedExpressionValue value)
    {
        if (value.Display.VariablesReference <= 0 ||
            !_values.TryGetValue(
                value.Display.VariablesReference,
                out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Expression '{value.Display.EvaluateName ?? value.Display.Name}' does not " +
                "identify a runtime object that can receive a method call.");
        }

        return handle.Pointer;
    }

    private static unsafe nint GetModuleFunction(nint module, uint methodToken)
    {
        nint function = 0;
        nint* functionAddress = &function;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugModuleAbi(module).GetFunctionFromToken(
                methodToken,
                (nint)functionAddress),
            "ICorDebugModule.GetFunctionFromToken");
        return RequirePointer(
            Volatile.Read(ref *functionAddress),
            "ICorDebugModule.GetFunctionFromToken");
    }

    private static unsafe nint CreateEvaluation(nint thread)
    {
        nint evaluation = 0;
        nint* evaluationAddress = &evaluation;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).CreateEval((nint)evaluationAddress),
            "ICorDebugThread.CreateEval");
        return RequirePointer(
            Volatile.Read(ref *evaluationAddress),
            "ICorDebugThread.CreateEval");
    }

    private static void ThrowIfFunctionEvaluationUnavailable(int hresult, string operation)
    {
        string? restriction = hresult switch
        {
            IllegalInStackOverflowHResult => "handling a stack overflow",
            IllegalAtGcUnsafePointHResult => "at a garbage-collection-unsafe point",
            IllegalInPrologHResult => "in a method prolog",
            IllegalInNativeCodeHResult => "in native code",
            IllegalInOptimizedCodeHResult => "in optimized code",
            _ => null
        };
        if (restriction is null)
        {
            CorDebugHResult.ThrowIfFailed(hresult, operation);
            return;
        }

        string guidance = hresult == IllegalInOptimizedCodeHResult
            ? " Relaunch with suppressJITOptimizations enabled when target-code evaluation " +
                "is required."
            : string.Empty;
        throw new InvalidOperationException(
            $"CoreCLR cannot execute a debugger function evaluation while the selected " +
            $"frame is {restriction}.{guidance} HRESULT 0x{hresult:X8}.");
    }
}
