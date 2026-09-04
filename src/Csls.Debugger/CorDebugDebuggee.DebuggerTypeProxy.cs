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

    private unsafe bool CompleteDebuggerTypeProxyEvaluation(
        ManagedFunctionEvaluation active,
        bool isException,
        DebugStopGeneration resultGeneration)
    {
        ManagedDebuggerTypeProxyEvaluation context = active.DebuggerTypeProxy ??
            throw new InvalidOperationException("No debugger type-proxy evaluation is active.");
        ManagedFunctionEvaluationResult? result = null;
        Exception? failure = null;
        nint value = 0;
        bool continues = false;
        try
        {
            ClearFrameHandles();
            if (active.AbortRequested)
            {
                failure = new OperationCanceledException(
                    "Debugger type-proxy presentation was canceled cooperatively.");
            }
            else
            {
                nint* valueAddress = &value;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugEvalAbi(active.Pointer).GetResult((nint)valueAddress),
                    "ICorDebugEval.GetResult");
                value = Volatile.Read(ref *valueAddress);
                if (!context.ConstructorCompleted)
                {
                    if (isException || value == 0)
                    {
                        result = CreateDebuggerTypeProxyFallback(active, resultGeneration);
                    }
                    else
                    {
                        context.ConstructorCompleted = true;
                        active.Receiver = CreateFunctionEvaluationHandle(value);
                        TryResolveDebuggerTypeProxyProperties(context, value);
                        if (TryContinueDebuggerTypeProxyPropertyEvaluation(active))
                        {
                            continues = true;
                            return true;
                        }

                        result = CreateDebuggerTypeProxySuccess(active, resultGeneration);
                    }
                }
                else
                {
                    RecordDebuggerTypeProxyPropertyResult(context, value, isException);
                    if (TryContinueDebuggerTypeProxyPropertyEvaluation(active))
                    {
                        continues = true;
                        return true;
                    }

                    result = CreateDebuggerTypeProxySuccess(active, resultGeneration);
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or
            OperationCanceledException)
        {
            failure = exception;
            if (_functionEvaluationDisabledReason is null &&
                exception is not OperationCanceledException)
            {
                try
                {
                    result = context.ConstructorCompleted
                        ? CreateDebuggerTypeProxySuccess(active, resultGeneration)
                        : CreateDebuggerTypeProxyFallback(active, resultGeneration);
                    failure = null;
                }
                catch (Exception recoveryException) when (
                    recoveryException is ArgumentException or InvalidOperationException or
                    IOException or UnauthorizedAccessException or BadImageFormatException)
                {
                    failure = new InvalidOperationException(
                        "Debugger type-proxy presentation failed and no stable value could " +
                        $"be recovered: {recoveryException.Message}",
                        recoveryException);
                }
            }
        }
        finally
        {
            if (value != 0)
            {
                _ = ComAbi.Release(value);
            }

            if (!continues)
            {
                CompleteDebuggerTypeProxyOperation(active, result, failure);
            }
        }

        return true;
    }

    private void TryResolveDebuggerTypeProxyProperties(
        ManagedDebuggerTypeProxyEvaluation context,
        nint proxyValue)
    {
        try
        {
            context.Properties.AddRange(
                _debuggerTypeProxyPropertyResolver.Resolve(proxyValue));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or OverflowException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Debugger proxy properties could not be resolved: {exception.Message}");
        }
    }

    private void RecordDebuggerTypeProxyPropertyResult(
        ManagedDebuggerTypeProxyEvaluation context,
        nint value,
        bool isException)
    {
        ManagedDebuggerTypeProxyPropertyBinding property = context.CurrentProperty ??
            throw new InvalidOperationException(
                "No debugger proxy property getter is active.");
        ManagedValueDisplay display;
        nint handle = 0;
        nint inspectedValue = 0;
        try
        {
            if (value == 0)
            {
                display = new ManagedValueDisplay(
                    "<error: the property getter returned no value>",
                    property.DeclaredType);
            }
            else if (isException)
            {
                ManagedValueDisplay exception = FormatRuntimeValue(value);
                display = new ManagedValueDisplay(
                    $"<error: {exception.Type}: {exception.Value}>",
                    property.DeclaredType);
            }
            else if (!TryDereferenceAndUnboxValue(value, out inspectedValue))
            {
                display = new ManagedValueDisplay("null", property.DeclaredType);
            }
            else
            {
                display = FormatRuntimeValue(inspectedValue);
                if (IsExpandable(inspectedValue))
                {
                    try
                    {
                        handle = CreateFunctionEvaluationHandle(value);
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or InvalidOperationException)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Debugger proxy property '{property.Name}' could not retain an " +
                            $"expandable value: {exception.Message}");
                    }
                }
            }
        }
        finally
        {
            if (inspectedValue != 0)
            {
                _ = ComAbi.Release(inspectedValue);
            }
        }

        context.PropertyResults.Add(new ManagedDebuggerTypeProxyPropertyResult(
            property.Name,
            property.BrowsingState,
            display,
            handle));
        context.CurrentProperty = null;
    }

    private bool TryContinueDebuggerTypeProxyPropertyEvaluation(
        ManagedFunctionEvaluation active)
    {
        ManagedDebuggerTypeProxyEvaluation context = active.DebuggerTypeProxy ??
            throw new InvalidOperationException("No debugger type-proxy evaluation is active.");
        if (context.NextPropertyIndex >= context.Properties.Count)
        {
            return false;
        }

        ManagedDebuggerTypeProxyPropertyBinding property =
            context.Properties[context.NextPropertyIndex++];
        nint nextEvaluation = 0;
        nint nextFunction = 0;
        nint completedEvaluation = active.Pointer;
        nint completedFunction = active.Function;
        try
        {
            nextEvaluation = CreateEvaluation(active.Thread);
            nextFunction = property.DetachFunction();
            active.Pointer = nextEvaluation;
            active.Function = nextFunction;
            active.ConstructsObject = false;
            active.Arguments = [];
            active.MethodCallScheduled = false;
            context.CurrentProperty = property;
            nextEvaluation = 0;
            nextFunction = 0;
            _ = ComAbi.Release(completedEvaluation);
            _ = ComAbi.Release(completedFunction);
            ScheduleNextFunctionEvaluationStage(active);
            int continueResult = new ICorDebugControllerAbi(_debugProcess)
                .Continue(fIsOutOfBand: 0);
            if (continueResult < 0)
            {
                _functionEvaluationDisabledReason =
                    "The debugger could not resume the target while evaluating a debugger " +
                    "proxy property. The target's evaluation state is uncertain; this " +
                    "debugger session must be disconnected.";
                CorDebugHResult.ThrowIfFailed(
                    continueResult,
                    "ICorDebugController.Continue");
            }

            return true;
        }
        finally
        {
            if (nextFunction != 0)
            {
                _ = ComAbi.Release(nextFunction);
            }

            if (nextEvaluation != 0)
            {
                _ = ComAbi.Release(nextEvaluation);
            }
        }
    }

    private ManagedFunctionEvaluationResult CreateDebuggerTypeProxySuccess(
        ManagedFunctionEvaluation active,
        DebugStopGeneration generation)
    {
        ManagedDebuggerTypeProxyEvaluation context = active.DebuggerTypeProxy ??
            throw new InvalidOperationException("No debugger type-proxy evaluation is active.");
        nint proxyValue = 0;
        try
        {
            if (!TryDereferenceValue(active.Receiver, out proxyValue))
            {
                return CreateDebuggerTypeProxyFallback(active, generation);
            }

            ManagedValueHandle proxy = RetainRuntimeValue(
                proxyValue,
                generation,
                evaluateName: null,
                frameId: null,
                context.ThreadId,
                ManagedValueView.ProxyBypassed,
                tupleCustomTypeInfo: null);
            proxy.ProxyRawValueReference = RetainDebuggerTypeProxyOriginal(
                active,
                generation,
                ManagedValueView.Raw).Id;
            proxy.ProxyProperties = MaterializeDebuggerTypeProxyProperties(
                context,
                generation);
            ManagedValueDisplay display = FormatRuntimeValue(proxyValue);
            ManagedValueReferences references = IsExpandable(proxyValue)
                ? new ManagedValueReferences(proxy.Id, proxy.MemoryReference)
                : default;
            return new ManagedFunctionEvaluationResult(
                new DebugEvaluateResult(
                    display.Value,
                    display.Type,
                    references.VariablesReference,
                    references.MemoryReference,
                    TargetCodeExecuted: true),
                proxy.Id,
                generation,
                DebuggerTypeProxyApplied: true);
        }
        finally
        {
            if (proxyValue != 0)
            {
                _ = ComAbi.Release(proxyValue);
            }
        }
    }

    private List<ManagedDebuggerTypeProxyPropertyPresentation>
        MaterializeDebuggerTypeProxyProperties(
            ManagedDebuggerTypeProxyEvaluation context,
            DebugStopGeneration generation)
    {
        var result = new List<ManagedDebuggerTypeProxyPropertyPresentation>(
            context.PropertyResults.Count);
        int visiblePropertyCount = 0;
        foreach (ManagedDebuggerTypeProxyPropertyResult property in context.PropertyResults)
        {
            nint handle = property.DetachHandle();
            nint value = 0;
            ManagedValueReferences references = default;
            IReadOnlyList<DebugVariableInfo>? rootHiddenVariables = null;
            try
            {
                if (handle != 0 && TryDereferenceAndUnboxValue(handle, out value))
                {
                    if (property.BrowsingState == ManagedDebuggerBrowsableState.RootHidden)
                    {
                        _ = TryExpandDebuggerTypeProxyRootHiddenProperty(
                            value,
                            generation,
                            MaximumExpandableValueCount - visiblePropertyCount,
                            out rootHiddenVariables);
                    }
                    else
                    {
                        references = RetainValue(
                            value,
                            generation,
                            evaluateName: null,
                            frameId: null);
                    }
                }

                result.Add(new ManagedDebuggerTypeProxyPropertyPresentation(
                    property.Name,
                    rootHiddenVariables ??
                    [
                        new DebugVariableInfo(
                            property.Name,
                            property.Display.Value,
                            property.Display.Type,
                            references.VariablesReference,
                            references.MemoryReference,
                            EvaluateName: null)
                    ]));
                visiblePropertyCount = checked(
                    visiblePropertyCount + (rootHiddenVariables?.Count ?? 1));
                if (visiblePropertyCount > MaximumExpandableValueCount)
                {
                    throw new InvalidOperationException(
                        $"Debugger proxy properties exceed the child limit of " +
                        $"{MaximumExpandableValueCount}.");
                }
            }
            finally
            {
                if (value != 0)
                {
                    _ = ComAbi.Release(value);
                }

                ReleaseFunctionEvaluationHandle(handle);
            }
        }

        return result;
    }

    private bool TryExpandDebuggerTypeProxyRootHiddenProperty(
        nint value,
        DebugStopGeneration generation,
        int maximumCount,
        out IReadOnlyList<DebugVariableInfo>? variables)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        variables = null;
        if (ComAbi.TryQueryInterface(
            value,
            ICorDebugArrayValueAbi.InterfaceId,
            out nint array))
        {
            try
            {
                variables = ExpandArray(
                    array,
                    parentEvaluateName: null,
                    frameId: null,
                    generation,
                    tupleCustomTypeInfo: null,
                    start: 0,
                    count: checked(maximumCount + 1));
                return true;
            }
            finally
            {
                _ = ComAbi.Release(array);
            }
        }

        if (!ComAbi.TryQueryInterface(
            value,
            ICorDebugObjectValueAbi.InterfaceId,
            out nint objectValue))
        {
            return false;
        }

        try
        {
            variables = ExpandObject(
                value,
                parentEvaluateName: null,
                frameId: null,
                generation,
                start: 0,
                count: checked(maximumCount + 1),
                ManagedValueView.Default,
                tupleCustomTypeInfo: null,
                proxyRawView: null,
                proxyProperties: null);
            return true;
        }
        finally
        {
            _ = ComAbi.Release(objectValue);
        }
    }

    private void CompleteDebuggerTypeProxyOperation(
        ManagedFunctionEvaluation active,
        ManagedFunctionEvaluationResult? result,
        Exception? failure)
    {
        _activeFunctionEvaluation = null;
        try
        {
            RestoreThreadStates(active);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            const string Reason =
                "Debugger type-proxy presentation completed, but the debugger could not " +
                "restore thread state safely. This debugger session must be disconnected.";
            failure = new InvalidOperationException(Reason, exception);
            _functionEvaluationDisabledReason = Reason;
        }
        finally
        {
            _managedCallback.EndFunctionEvaluation();
            ReleaseFunctionEvaluationResources(active);
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
    }

    private static void ReleaseDebuggerTypeProxyPropertyResources(
        ManagedDebuggerTypeProxyEvaluation context)
    {
        foreach (ManagedDebuggerTypeProxyPropertyBinding property in context.Properties)
        {
            property.Release();
        }

        foreach (ManagedDebuggerTypeProxyPropertyResult property in context.PropertyResults)
        {
            ReleaseFunctionEvaluationHandle(property.DetachHandle());
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
