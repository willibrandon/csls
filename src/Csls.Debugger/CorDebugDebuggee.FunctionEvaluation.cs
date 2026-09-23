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
    internal Task? ActiveFunctionEvaluationCompletion =>
        _activeFunctionEvaluation?.Completion.Task;

    /// <summary>
    /// Gets the actor-owned evaluation stage for deadline diagnostics without calling into the running target.
    /// </summary>
    internal string FunctionEvaluationDescription =>
        _activeFunctionEvaluation?.OperationDescription ?? "publishing its completed result";

    /// <summary>
    /// Starts one managed-method evaluation and resumes only its selected managed thread.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier for the visible stop.</param>
    /// <param name="plan">The validated invocation expression.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <param name="property">The already-bound root property receiver and exact getter, when applicable.</param>
    /// <returns>The result completed by the matching CoreCLR evaluation callback.</returns>
    internal Task<ManagedFunctionEvaluationResult> BeginFunctionEvaluationAsync(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation,
        ManagedPropertyEvaluation? property = null)
    {
        _managedCallback.ThrowIfRuntimeFailed();
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
        if (property is not null)
        {
            if (!_expressionEvaluationOptions.AllowImplicitFuncEval)
            {
                throw new InvalidOperationException("Automatic property evaluation is disabled by allowImplicitFuncEval.");
            }
            if (operation.Kind != DebugExpressionNodeKind.MemberAccess)
            {
                throw new InvalidDataException("A prepared property requires a member-access expression root.");
            }
            operation = operation with { Kind = DebugExpressionNodeKind.Invocation, Text = property.Getter.MethodName };
        }
        bool materializesString = operation is
        {
            Kind: DebugExpressionNodeKind.Literal,
            TypeName: "string"
        };
        bool convertsValue = operation.Kind == DebugExpressionNodeKind.Conversion;
        if (operation.Kind is not DebugExpressionNodeKind.Invocation and
            not DebugExpressionNodeKind.ObjectCreation &&
            !convertsValue &&
            !materializesString)
        {
            throw new InvalidDataException(
                "Target-code evaluation requires an invocation, object creation, " +
                "user-defined conversion, or string-materialization root.");
        }

        bool constructsObject = operation.Kind == DebugExpressionNodeKind.ObjectCreation;
        int argumentOffset = constructsObject || convertsValue ? 0 : 1;
        int argumentCount = materializesString
            ? 1
            : convertsValue
                ? 1
                : operation.Children.Count - argumentOffset;
        if (argumentCount > MaximumFunctionEvaluationArgumentCount)
        {
            throw new NotSupportedException(
                $"Managed function evaluation supports at most " +
                $"{MaximumFunctionEvaluationArgumentCount} method arguments.");
        }

        ManagedExpressionValue? receiver = null;
        var suppliedArguments = new ManagedExpressionValue[argumentCount];
        var constantArguments = new ManagedExpressionValue?[argumentCount];
        string?[] argumentNames = new string?[argumentCount];
        try
        {
            if (materializesString)
            {
                suppliedArguments[0] = ManagedExpressionValueFactory.FromLiteral(operation);
            }
            else
            {
                if (!constructsObject && !convertsValue &&
                    !TryResolveStaticReceiver(frame, operation.Children[0], out _))
                {
                    receiver = property?.Receiver ?? EvaluateNode(frame, plan, operation.Children[0], generation);
                }

                for (int index = 0; index < suppliedArguments.Length; index++)
                {
                    DebugExpressionNode argumentNode = operation.Children[index + argumentOffset];
                    if (argumentNode.Kind == DebugExpressionNodeKind.NamedArgument)
                    {
                        argumentNames[index] = argumentNode.Text;
                        argumentNode = argumentNode.Children[0];
                    }

                    suppliedArguments[index] = EvaluateNode(
                        frame,
                        plan,
                        argumentNode,
                        generation);
                    if (plan.Language == DebugExpressionLanguage.VisualBasic &&
                        argumentNode is { Kind: DebugExpressionNodeKind.Literal, TypeName: null })
                    {
                        suppliedArguments[index] = ManagedExpressionValueFactory.FromContextualDefault();
                    }

                    if (suppliedArguments[index].IsContextualDefault)
                    {
                        constantArguments[index] = suppliedArguments[index];
                    }

                    if (argumentNode.Kind == DebugExpressionNodeKind.Literal &&
                        suppliedArguments[index].Scalar is int or long)
                    {
                        constantArguments[index] = suppliedArguments[index];
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Managed function evaluation failed while binding its receiver or arguments: " +
                exception.Message,
                exception);
        }

        bool materializeReceiver = receiver is { IsZeroValueTypeDefault: true };
        nint receiverValue = receiver is null || materializeReceiver ? 0 : GetRuntimeValue(receiver);
        nint dereferencedReceiver = 0;
        nint objectValue = 0;
        nint function = 0;
        nint thread = 0;
        nint evaluation = 0;
        nint receiverHandle = 0;
        bool receiverIsHeapHandle = true;
        nint[] callTypeArguments = [];
        nint[] runtimeArguments = new nint[argumentCount];
        bool[] runtimeArgumentIsHeapHandle = new bool[argumentCount];
        bool argumentHandlesTransferred = false;
        bool callbackEvaluationActive = false;
        bool callScheduled = false;
        string setupPhase = "resolving the invocation receiver";
        try
        {
            if (receiverValue != 0)
            {
                dereferencedReceiver = DereferenceValue(receiverValue);
                if (ManagedRuntimeValueIdentity.GetElementType(dereferencedReceiver) is not (0x14 or 0x1d) &&
                    !ComAbi.TryQueryInterface(
                    dereferencedReceiver,
                    ICorDebugObjectValueAbi.InterfaceId,
                    out objectValue))
                {
                    throw new InvalidOperationException(
                        "The invocation receiver is not a managed object value.");
                }
            }

            setupPhase = "selecting the CoreCLR evaluation thread";
            thread = GetThread(frame.ThreadId);
            setupPhase = "resolving the runtime method";
            ManagedBoundType? declaredResultType;
            ManagedUserDefinedConversion? explicitConversion = null;
            if (materializesString)
            {
                declaredResultType = _boundTypes.Bind(
                    ManagedMetadataTypeSignatureProvider.Instance.GetPrimitiveType(
                        System.Reflection.Metadata.PrimitiveTypeCode.String), [], [], thread);
            }
            else
            {
                ManagedBoundType?[] argumentTypes = BindFunctionEvaluationArgumentTypes(
                    suppliedArguments, plan.Language, thread);
                ManagedFunctionBinding binding = convertsValue
                    ? ResolveUserDefinedExplicitConversion(
                        argumentTypes[0] ?? throw new InvalidOperationException(
                            "A null literal has no user-defined conversion source type."),
                        BindConversionTarget(
                            operation.TypeName ?? throw new InvalidDataException(
                                "A conversion expression has no target type."),
                            plan.Language,
                            thread),
                        plan.Language,
                        thread,
                        out explicitConversion)
                    : constructsObject
                    ? ResolveConstructor(operation.Text!, plan.Language, argumentTypes,
                        constantArguments, argumentNames, thread)
                    : materializeReceiver
                        ? ResolveBoundInstanceFunction(
                            receiver!.DeclaredType ?? throw new InvalidOperationException(
                                "The temporary receiver has no exact declared type."),
                            operation.Text!, plan.Language, argumentTypes, constantArguments,
                            argumentNames, thread)
                    : receiverValue == 0
                        ? ResolveStaticFunction(operation.Children[0], operation.Text!, plan.Language,
                            argumentTypes, constantArguments, argumentNames, thread)
                        : ResolveInstanceFunction(dereferencedReceiver, operation.Text!, plan.Language,
                            argumentTypes, constantArguments, argumentNames, thread,
                            property?.DeclaringType ?? receiver?.ExplicitReceiverType ?? receiver?.DeclaredType,
                            property?.Getter.MethodToken);
                function = binding.Function;
                callTypeArguments = binding.TypeArguments;
                declaredResultType = binding.DeclaredResultType;
                var referenceConversions = new ManagedReferenceConversion(_boundTypes);
                var userDefinedConversions = new ManagedUserDefinedConversionResolver(
                    _boundTypes, thread, plan.Language);
                for (int index = 0; index < suppliedArguments.Length; index++)
                {
                    ManagedBoundType? sourceType = argumentTypes[index];
                    ManagedBoundType parameterType = binding.ParameterTypes[index];
                    if (convertsValue)
                    {
                        suppliedArguments[index] = PrepareUserDefinedConversionInput(
                            suppliedArguments[index],
                            sourceType ?? throw new InvalidOperationException(
                                "A null literal has no user-defined conversion source type."),
                            explicitConversion ?? throw new InvalidOperationException(
                                "An explicit conversion has no selected loaded operator."),
                            referenceConversions,
                            thread);
                    }
                    else if (suppliedArguments[index].IsContextualDefault)
                    {
                        suppliedArguments[index] = ManagedFunctionImplicitDefaults.TryCreateContextual(
                            parameterType, _boundTypes, thread) ?? throw new InvalidOperationException(
                                $"A default literal cannot be materialized as '{parameterType.DisplayName}'.");
                    }
                    else if (sourceType is not null && !sourceType.IsSameType(parameterType) &&
                        referenceConversions.IsImplicitBoxing(sourceType, parameterType, thread))
                    {
                        bool boxesNullable = _boundTypes.IsCoreType(
                            sourceType, "System.Nullable`1", thread);
                        bool boxesNullableAsNull = boxesNullable &&
                            IsNullableBoxingEmpty(suppliedArguments[index], sourceType, thread);
                        suppliedArguments[index] = suppliedArguments[index] with
                        {
                            DeclaredType = sourceType,
                            RequiresNullableMaterialization = false,
                            RequiresBoxing = !boxesNullableAsNull,
                            BoxingType = boxesNullable
                                ? sourceType.TypeArguments[0]
                                : sourceType,
                            BoxesNullableAsNull = boxesNullableAsNull
                        };
                    }
                    else if (sourceType is not null && !sourceType.IsSameType(parameterType) &&
                        ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
                            sourceType, parameterType, plan.Language))
                    {
                        suppliedArguments[index] = ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                            suppliedArguments[index], sourceType, parameterType, plan.Language);
                    }
                    else if (sourceType is not null && !sourceType.IsSameType(parameterType) &&
                        constantArguments[index] is ManagedExpressionValue constant &&
                        ManagedPrimitiveConversionEvaluator.IsImplicitConstantInvocationConversion(
                            constant, sourceType, parameterType, plan.Language))
                    {
                        suppliedArguments[index] = ManagedPrimitiveConversionEvaluator.ConvertInvocationConstant(
                            constant, sourceType, parameterType, plan.Language);
                    }
                    else if (sourceType is not null && !sourceType.IsSameType(parameterType) &&
                        !referenceConversions.IsImplicit(sourceType, parameterType, thread))
                    {
                        ManagedUserDefinedConversion conversion = userDefinedConversions.Resolve(
                            sourceType, parameterType) ?? throw new InvalidOperationException(
                            $"The selected method has no loaded implicit conversion from " +
                            $"'{sourceType.DisplayName}' to '{parameterType.DisplayName}'.");
                        suppliedArguments[index] = PrepareUserDefinedConversionArgument(
                            suppliedArguments[index], sourceType, conversion, referenceConversions,
                            thread);
                    }

                    if (suppliedArguments[index].Scalar is decimal &&
                        _boundTypes.IsCoreType(parameterType, "System.Decimal", thread))
                    {
                        suppliedArguments[index] = suppliedArguments[index] with
                        {
                            DeclaredType = parameterType
                        };
                    }
                }

                if (binding.ParameterSourceIndices.Length != binding.OptionalArguments.Length ||
                    binding.ParameterSourceIndices.Length > MaximumFunctionEvaluationArgumentCount)
                {
                    throw new InvalidDataException(
                        "The resolved method argument map does not match the supplied call.");
                }

                var parameterOrderedArguments = new ManagedExpressionValue[binding.ParameterSourceIndices.Length];
                for (int index = 0; index < parameterOrderedArguments.Length; index++)
                {
                    int sourceIndex = binding.ParameterSourceIndices[index];
                    parameterOrderedArguments[index] = sourceIndex >= 0
                        ? suppliedArguments[sourceIndex]
                        : binding.OptionalArguments[index] ?? throw new InvalidDataException(
                            "The resolved method omitted a required argument.");
                }

                suppliedArguments = parameterOrderedArguments;
                runtimeArguments = new nint[suppliedArguments.Length];
                runtimeArgumentIsHeapHandle = new bool[suppliedArguments.Length];
            }

            setupPhase = "creating the CoreCLR evaluation";
            evaluation = CreateEvaluation(thread);
            if (receiverValue != 0)
            {
                (receiverHandle, receiverIsHeapHandle) =
                    RetainFunctionEvaluationArgument(receiverValue);
            }
            for (int index = 0; index < suppliedArguments.Length; index++)
            {
                if (suppliedArguments[index] is
                    { RuntimeValueReference: > 0 } argument &&
                    (argument.DeclaredType is { IsReference: true } ||
                     !argument.HasScalar || argument.Scalar is string ||
                     argument.RequiresNullableMaterialization || argument.RequiresUnboxing))
                {
                    (runtimeArguments[index], runtimeArgumentIsHeapHandle[index]) =
                        RetainFunctionEvaluationArgument(GetRuntimeValue(argument));
                }
            }

            Dictionary<int, int> threadStates = [];
            var active = new ManagedFunctionEvaluation
            {
                Pointer = evaluation,
                Function = function,
                TypeArguments = callTypeArguments,
                DeclaredResultType = declaredResultType,
                ExplicitUserDefinedConversion = explicitConversion,
                ResultTupleCustomTypeInfo = property?.Getter.TupleCustomTypeInfo,
                ResultFrameId = frame.Id,
                Thread = thread,
                Receiver = receiverHandle,
                ReceiverValue = receiver,
                ReceiverIsHeapHandle = receiverIsHeapHandle,
                ConstructsObject = constructsObject,
                MaterializesString = materializesString,
                Arguments = suppliedArguments,
                RuntimeArguments = runtimeArguments,
                RuntimeArgumentIsHeapHandle = runtimeArgumentIsHeapHandle,
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
            ContinueForFunctionEvaluation();
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
                        if (RuntimeFailure is null)
                        {
                            RestoreThreadStates(active);
                        }
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

            _managedCallback.ThrowIfRuntimeFailed();
            throw new InvalidOperationException(
                $"Managed function evaluation failed while {setupPhase}: {failure.Message}",
                failure);
        }
        finally
        {
            if (!argumentHandlesTransferred)
            {
                for (int index = 0; index < runtimeArguments.Length; index++)
                {
                    ReleaseFunctionEvaluationArgument(
                        runtimeArguments[index], runtimeArgumentIsHeapHandle[index]);
                }

                ReleaseFunctionEvaluationArgument(receiverHandle, receiverIsHeapHandle);
                foreach (nint typeArgument in callTypeArguments)
                {
                    ReleaseFunctionEvaluationPointer(typeArgument);
                }
            }

            if (evaluation != 0)
            {
                _ = ComAbi.Release(evaluation);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }

            ReleaseFunctionEvaluationPointer(function);

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

        if (active.DebuggerTypeProxy is not null)
        {
            return CompleteDebuggerTypeProxyEvaluation(
                active,
                isException,
                resultGeneration);
        }

        if (active.ResultsView is not null)
        {
            return CompleteResultsViewEvaluation(active, isException, resultGeneration);
        }

        Exception? stageFailure = null;
        if (!active.MethodCallScheduled && !active.AbortRequested && !isException)
        {
            try
            {
                if (active.PendingStructuredReceiver)
                {
                    ContinueAfterStructuredReceiverAllocation(active);
                }
                else if (active.PendingStructuredArgumentIndex >= 0)
                {
                    ContinueAfterStructuredArgumentAllocation(active);
                }
                else if (active.PendingUserDefinedConversionArgumentIndex >= 0)
                {
                    ContinueAfterUserDefinedConversion(active);
                }
                else
                {
                    ContinueAfterStringMaterialization(active);
                }
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or IOException or
                UnauthorizedAccessException or BadImageFormatException)
            {
                stageFailure = new InvalidOperationException(
                    "Managed function evaluation failed while materializing a value: " +
                    exception.Message,
                    exception);
            }
        }

        _activeFunctionEvaluation = null;
        ManagedFunctionEvaluationResult? result = null;
        Exception? failure = stageFailure;
        nint value = 0;
        bool handlesCleared = false;
        try
        {
            ClearFrameHandles(preserveFrameIdentity: true);
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
                    result = new ManagedFunctionEvaluationResult(
                        new DebugEvaluateResult(
                            string.Empty,
                            "void",
                            VariablesReference: 0,
                            MemoryReference: null,
                            TargetCodeExecuted: true),
                        RuntimeValueReference: 0,
                        resultGeneration);
                }
                else
                {
                    ManagedValueDisplay display = FormatRuntimeValue(value, isException ? null : active.ResultTupleCustomTypeInfo);
                    if (isException)
                    {
                        failure = new InvalidOperationException(
                            $"Managed function evaluation threw {display.Type}: {display.Value}");
                    }
                    else if (TryCreateExplicitUserDefinedConversionResult(
                        value, active, resultGeneration, out ManagedFunctionEvaluationResult converted))
                    {
                        result = converted;
                    }
                    else
                    {
                        (int runtimeValueReference, ManagedValueReferences references) =
                            RetainFunctionEvaluationValue(
                            value,
                            resultGeneration,
                            active.ThreadId,
                            ManagedValueView.Default,
                            active.ResultTupleCustomTypeInfo,
                            active.ResultFrameId);

                        result = new ManagedFunctionEvaluationResult(
                            WithArrayChildCounts(new DebugEvaluateResult(
                                display.Value,
                                display.Type,
                                references.VariablesReference,
                                references.MemoryReference,
                                TargetCodeExecuted: true)),
                            runtimeValueReference,
                            resultGeneration,
                            DebuggerTypeProxyApplied: false,
                            DeclaredType: active.DeclaredResultType);
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
                    ClearFrameHandles(preserveFrameIdentity: true);
                }

                try
                {
                    if (RuntimeFailure is null)
                    {
                        RestoreThreadStates(active);
                    }
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

        failure = RuntimeFailure ?? failure;
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
    /// <param name="runtimeAvailable">Whether the runtime permits strong-handle disposal.</param>
    internal void FailFunctionEvaluation(Exception exception, bool runtimeAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ManagedFunctionEvaluation? active = _activeFunctionEvaluation;
        if (active is null)
        {
            return;
        }

        _activeFunctionEvaluation = null;
        _managedCallback.EndFunctionEvaluation();
        ReleaseFunctionEvaluationResources(active, runtimeAvailable);
        _ = active.Completion.TrySetException(exception);
    }

    private nint GetRuntimeValue(ManagedExpressionValue value)
    {
        if (value.RuntimeValueReference <= 0 ||
            !_values.TryGetValue(
                value.RuntimeValueReference,
                out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Expression '{value.Display.EvaluateName ?? value.Display.Name}' does not " +
                "identify a runtime object that can receive a method call.");
        }

        ValidateValueLifetime(handle);
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

    private static void ReleaseFunctionEvaluationPointer(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
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
