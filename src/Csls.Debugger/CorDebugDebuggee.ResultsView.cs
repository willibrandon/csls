using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Coordinates explicitly authorized enumeration through the runtime's Results View.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    DebugVariableInfo? IManagedObjectExpansionServices.TryRetainResultsView(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        int? threadId,
        ManagedValueView view,
        ManagedValueOrigin? origin,
        ManagedResultsViewLifetime? lifetime)
    {
        if (view is not (ManagedValueView.Default or ManagedValueView.Raw or
            ManagedValueView.ProxyBypassed) ||
            (threadId ?? GetValueThreadId(frameId)) is not int selectedThread)
        {
            return null;
        }

        nint thread = GetThread(selectedThread);
        nint candidate = 0;
        try
        {
            candidate = GetResultsViewTarget(value, ref origin);
            if (candidate == 0)
            {
                return null;
            }

            if (view == ManagedValueView.Default &&
                _debuggerTypeProxyResolver.TryResolve(
                    candidate,
                    thread,
                    out ManagedDebuggerTypeProxyBinding? proxy) && proxy is not null)
            {
                ReleaseFunctionEvaluationPointer(proxy.Function);
                foreach (nint typeArgument in proxy.TypeArguments)
                {
                    ReleaseFunctionEvaluationPointer(typeArgument);
                }

                return null;
            }

            if (!_resultsViewResolver.CanResolve(candidate, thread))
            {
                return null;
            }

            if (_resultsViewSnapshot is ManagedResultsViewSnapshot snapshot &&
                snapshot.Generation == generation && !snapshot.Lifetime.IsRetired &&
                MatchesResultsViewReceiver(candidate, origin, snapshot.Receiver))
            {
                return GetResultsViewSnapshot(snapshot.VariablesReference, generation);
            }

            ManagedValueHandle retained = RetainRuntimeValue(
                    candidate,
                    generation,
                    evaluateName,
                    frameId,
                    selectedThread,
                    ManagedValueView.ResultsView,
                    tupleCustomTypeInfo: null,
                    origin,
                    lifetime);
            return new DebugVariableInfo(
                "Results View", "Expanding the Results View will enumerate the IEnumerable",
                string.Empty, retained.Id, null, null, DebugVariablePresentationKind.ResultsView);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(candidate);
            _ = ComAbi.Release(thread);
        }
    }

    /// <summary>
    /// Starts explicit enumeration of a current-generation lazy Results View.
    /// </summary>
    /// <param name="variablesReference">The requested variable-container identifier.</param>
    /// <param name="generation">The current stop generation.</param>
    /// <param name="completion">Receives completion of the enumeration operation.</param>
    /// <returns>True when the reference identifies an eligible lazy view.</returns>
    internal bool TryBeginResultsViewEvaluation(
        int variablesReference,
        DebugStopGeneration generation,
        out Task<ManagedFunctionEvaluationResult>? completion)
    {
        _managedCallback.ThrowIfRuntimeFailed();
        completion = null;
        if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? handle) ||
            handle.View != ManagedValueView.ResultsView)
        {
            return false;
        }

        ValidateGeneration(variablesReference, handle.Generation, generation);
        ValidateValueLifetime(handle);
        if (_activeFunctionEvaluation is not null)
        {
            throw new InvalidOperationException("Only one managed function evaluation may run at a time.");
        }

        if (_functionEvaluationDisabledReason is not null)
        {
            throw new InvalidOperationException(_functionEvaluationDisabledReason);
        }

        int threadId = handle.ThreadId ??
            throw new InvalidOperationException("Results View has no evaluation thread.");
        nint thread = GetThread(threadId);
        nint value = 0;
        ManagedResultsViewBinding? binding = null;
        try
        {
            if (!TryDereferenceAndUnboxValue(handle.Pointer, out value) ||
                !_resultsViewResolver.TryResolve(value, thread, out binding) ||
                binding is null)
            {
                throw new InvalidOperationException("The target's Results View is no longer available.");
            }

            completion = BeginResultsViewEvaluationAsync(handle, threadId, binding);
            return true;
        }
        finally
        {
            binding?.Release();
            ReleaseFunctionEvaluationPointer(value);
            _ = ComAbi.Release(thread);
        }
    }

    private Task<ManagedFunctionEvaluationResult> BeginResultsViewEvaluationAsync(
        ManagedValueHandle handle,
        int threadId,
        ManagedResultsViewBinding binding)
    {
        nint thread = 0;
        nint evaluation = 0;
        nint targetHandle = 0;
        nint function = 0;
        nint itemsGetter = 0;
        nint[] typeArguments = [];
        nint retainedEnumerable = 0;
        nint constructor = 0;
        nint[] constructorTypeArguments = [];
        bool callbackActive = false;
        bool callScheduled = false;
        using var receiverOwner = new DisposableOwner<ManagedResultsViewReceiverIdentity>();
        try
        {
            receiverOwner.Acquire(() => CaptureResultsViewReceiver(handle));
            thread = GetThread(threadId);
            evaluation = CreateEvaluation(thread);
            retainedEnumerable = RetainResultsViewStructReceiver(handle.Pointer);
            if (retainedEnumerable != 0)
            {
                function = ResolveResultsViewBoxingFunction(retainedEnumerable);
                constructor = binding.DetachConstructor();
                constructorTypeArguments = binding.DetachTypeArguments();
            }
            else
            {
                targetHandle = CreateFunctionEvaluationHandle(handle.Pointer);
                function = binding.DetachConstructor();
                typeArguments = binding.DetachTypeArguments();
            }

            itemsGetter = binding.DetachItemsGetter();
            var threadStates = new Dictionary<int, int>();
            ManagedValueDisplay runtimeValue = FormatRuntimeValuePair(
                handle.Pointer, debuggerDisplayDepth: 0, handle.TupleCustomTypeInfo).Runtime;
            var enumerableArgument = new ManagedExpressionValue(
                new DebugVariableInfo("$enumerable", string.Empty, string.Empty,
                    handle.Id, null, handle.EvaluateName),
                Scalar: null,
                HasScalar: false,
                Type: runtimeValue.Type,
                RuntimeValueReference: handle.Id);
            var active = new ManagedFunctionEvaluation
            {
                Pointer = evaluation,
                Function = function,
                TypeArguments = typeArguments,
                Thread = thread,
                Receiver = retainedEnumerable,
                ConstructsObject = retainedEnumerable == 0,
                MaterializesString = false,
                Arguments = retainedEnumerable == 0 ? [enumerableArgument] : [],
                RuntimeArguments = [targetHandle],
                ThreadId = threadId,
                ThreadStates = threadStates,
                ResultsView = new ManagedResultsViewEvaluation(
                    itemsGetter, enumerableArgument, retainedEnumerable,
                    constructor, constructorTypeArguments, receiverOwner.Value ??
                        throw new InvalidOperationException("The enumerable receiver was not retained."))
            };
            _activeFunctionEvaluation = active;
            _ = receiverOwner.Detach();
            evaluation = 0;
            function = 0;
            thread = 0;
            targetHandle = 0;
            typeArguments = [];
            itemsGetter = 0;
            retainedEnumerable = 0;
            constructor = 0;
            constructorTypeArguments = [];
            SuspendOtherThreads(threadId, threadStates);
            _managedCallback.BeginFunctionEvaluation();
            callbackActive = true;
            ScheduleNextFunctionEvaluationStage(active);
            callScheduled = true;
            ContinueForFunctionEvaluation();
            return active.Completion.Task.WaitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (callScheduled)
            {
                _functionEvaluationDisabledReason =
                    "The debugger could not resume the target after scheduling Results View. " +
                    "The evaluation state is uncertain; this debugger session must be disconnected.";
            }

            if (_activeFunctionEvaluation is ManagedFunctionEvaluation active)
            {
                _activeFunctionEvaluation = null;
                try
                {
                    if (RuntimeFailure is null)
                    {
                        RestoreThreadStates(active);
                    }
                }
                finally
                {
                    ReleaseFunctionEvaluationResources(active);
                }
            }

            if (callbackActive)
            {
                _managedCallback.EndFunctionEvaluation();
            }

            _managedCallback.ThrowIfRuntimeFailed();
            throw new InvalidOperationException(
                _functionEvaluationDisabledReason ??
                $"Results View could not start: {exception.Message}",
                exception);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(itemsGetter);
            ReleaseFunctionEvaluationPointer(retainedEnumerable);
            ReleaseFunctionEvaluationPointer(constructor);
            ReleaseFunctionEvaluationHandle(targetHandle);
            foreach (nint typeArgument in constructorTypeArguments)
            {
                ReleaseFunctionEvaluationPointer(typeArgument);
            }
            foreach (nint typeArgument in typeArguments)
            {
                ReleaseFunctionEvaluationPointer(typeArgument);
            }

            ReleaseFunctionEvaluationPointer(function);
            ReleaseFunctionEvaluationPointer(evaluation);
            ReleaseFunctionEvaluationPointer(thread);
        }
    }

    private unsafe bool CompleteResultsViewEvaluation(
        ManagedFunctionEvaluation active,
        bool isException,
        DebugStopGeneration generation)
    {
        ManagedResultsViewEvaluation context = active.ResultsView ??
            throw new InvalidOperationException("No Results View evaluation is active.");
        ManagedFunctionEvaluationResult? result = null;
        Exception? failure = null;
        nint value = 0;
        bool continues = false;
        try
        {
            ClearFrameHandles(preserveFrameIdentity: true);
            if (active.AbortRequested)
            {
                throw new OperationCanceledException("Results View enumeration was canceled cooperatively.");
            }

            nint* valueAddress = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(active.Pointer).GetResult((nint)valueAddress),
                "ICorDebugEval.GetResult");
            value = RequirePointer(Volatile.Read(ref *valueAddress), "ICorDebugEval.GetResult");
            if (isException)
            {
                nint exceptionValue = DereferenceValue(value);
                try
                {
                    if (context.ConstructorCompleted &&
                        _resultsViewResolver.IsEmptyEnumerationException(exceptionValue, active.Function))
                    {
                        result = RetainResultsViewResult(
                            value, generation, active.ThreadId, empty: true, context.Lifetime);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            FormatResultsViewException(exceptionValue, generation));
                    }
                }
                finally
                {
                    _ = ComAbi.Release(exceptionValue);
                }
            }
            else if (context.RetainedEnumerableValue != 0 && !context.EnumerableBoxingCompleted)
            {
                ContinueResultsViewConstruction(active, context, value);
                continues = true;
            }
            else if (!context.ConstructorCompleted)
            {
                active.Receiver = CreateFunctionEvaluationHandle(value);
                context.ConstructorCompleted = true;
                ContinueResultsViewEvaluation(active, context);
                continues = true;
            }
            else
            {
                result = RetainResultsViewResult(
                    value, generation, active.ThreadId, empty: false, context.Lifetime);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or OperationCanceledException)
        {
            failure = exception;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
            if (!continues)
            {
                CompletePresentationOperation(active, result, failure);
            }
        }

        return true;
    }

    private void ContinueResultsViewEvaluation(
        ManagedFunctionEvaluation active,
        ManagedResultsViewEvaluation context)
    {
        nint nextEvaluation = CreateEvaluation(active.Thread);
        nint nextFunction = 0;
        try
        {
            nextFunction = context.DetachItemsGetter();
            _ = ComAbi.Release(active.Pointer);
            _ = ComAbi.Release(active.Function);
            active.Pointer = nextEvaluation;
            active.Function = nextFunction;
            nextEvaluation = 0;
            nextFunction = 0;
            active.ConstructsObject = false;
            active.Arguments = [];
            active.MethodCallScheduled = false;
            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after scheduling enumeration. " +
                "The evaluation state is uncertain; this debugger session must be disconnected.");
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(nextFunction);
            ReleaseFunctionEvaluationPointer(nextEvaluation);
        }
    }

    private ManagedFunctionEvaluationResult RetainResultsViewResult(
        nint value,
        DebugStopGeneration generation,
        int threadId,
        bool empty,
        ManagedResultsViewLifetime lifetime)
    {
        ManagedValueHandle handle = RetainRuntimeValue(
            value,
            generation,
            evaluateName: null,
            frameId: null,
            threadId,
            ManagedValueView.ResultsMaterialized,
            tupleCustomTypeInfo: null,
            lifetime: lifetime);
        if (empty)
        {
            handle.SyntheticVariables =
            [
                new DebugVariableInfo("Empty", "\"Enumeration yielded no results\"", "string",
                    0, null, null, DebugVariablePresentationKind.ReadOnlyString)
            ];
        }

        return new ManagedFunctionEvaluationResult(
            new DebugEvaluateResult(string.Empty, string.Empty, handle.Id, null, TargetCodeExecuted: true),
            handle.Id,
            generation);
    }

    /// <summary>
    /// Describes a retained enumerable snapshot without re-executing target code.
    /// </summary>
    /// <param name="variablesReference">The retained materialized Results View handle.</param>
    /// <param name="generation">The current stop generation.</param>
    /// <returns>The non-lazy replacement variable and its exact child counts.</returns>
    internal DebugVariableInfo GetResultsViewSnapshot(
        int variablesReference,
        DebugStopGeneration generation)
    {
        if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? handle) ||
            handle.View != ManagedValueView.ResultsMaterialized)
        {
            throw new InvalidOperationException(
                "The Results View snapshot is stale or unknown.");
        }

        ValidateGeneration(variablesReference, handle.Generation, generation);
        ValidateValueLifetime(handle);
        if (handle.SyntheticVariables is IReadOnlyList<DebugVariableInfo> syntheticVariables)
        {
            return new DebugVariableInfo(
                "Results View", string.Empty, string.Empty, handle.Id, null, null,
                DebugVariablePresentationKind.ResultsSnapshot,
                NamedVariables: syntheticVariables.Count,
                IndexedVariables: 0);
        }

        nint value = DereferenceValue(handle.Pointer);
        nint array = 0;
        try
        {
            array = ComAbi.QueryInterface(value, ICorDebugArrayValueAbi.InterfaceId);
            uint elementCount = GetArrayElementCount(new ICorDebugArrayValueAbi(array));
            if (elementCount > MaximumExpandableValueCount)
            {
                throw new InvalidOperationException(
                    $"The array exceeds the debugger element limit of {MaximumExpandableValueCount}.");
            }

            ManagedValueDisplay display = FormatRuntimeValue(value);
            return new DebugVariableInfo(
                "Results View", display.Value, display.Type, handle.Id, null, null,
                DebugVariablePresentationKind.ResultsSnapshot,
                NamedVariables: 0,
                IndexedVariables: checked((int)elementCount));
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(array);
            _ = ComAbi.Release(value);
        }
    }

    private string FormatResultsViewException(nint exception, DebugStopGeneration generation)
    {
        ManagedValueDisplay display = FormatRuntimeValue(exception);
        List<DebugVariableInfo> fields = ExpandObject(
            exception, parentEvaluateName: null, frameId: null, generation,
            start: 0, count: 256, ManagedValueView.ProxyRaw,
            tupleCustomTypeInfo: null, proxyRawView: null, proxyStaticView: null, proxyProperties: null);
        DebugVariableInfo? message = fields.FirstOrDefault(static field =>
            field.Name == "_message" && field.Type == "string" && field.Value != "null");
        return $"Results View enumeration threw {display.Type}: {message?.Value ?? display.Value}";
    }
}
