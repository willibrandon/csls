using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Schedules target string allocation and the final managed method call.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe void ScheduleNextFunctionEvaluationStage(
        ManagedFunctionEvaluation evaluation)
    {
        if (evaluation.MaterializesString)
        {
            if (evaluation.MethodCallScheduled ||
                evaluation.Arguments is not [{ HasScalar: true, Scalar: string text }])
            {
                throw new InvalidOperationException(
                    "The managed string-materialization evaluation is invalid.");
            }

            nint evaluation2 = 0;
            try
            {
                evaluation2 = ComAbi.QueryInterface(
                    evaluation.Pointer,
                    ICorDebugEval2Abi.InterfaceId);
                fixed (char* textAddress = text)
                {
                    ThrowIfFunctionEvaluationUnavailable(
                        new ICorDebugEval2Abi(evaluation2).NewStringWithLength(
                            (nint)textAddress,
                            checked((uint)text.Length)),
                        "ICorDebugEval2.NewStringWithLength");
                }

                evaluation.MethodCallScheduled = true;
                return;
            }
            finally
            {
                if (evaluation2 != 0)
                {
                    _ = ComAbi.Release(evaluation2);
                }
            }
        }

        if (evaluation.Receiver == 0 && evaluation.ReceiverValue is { IsZeroValueTypeDefault: true })
        {
            ScheduleStructuredReceiverAllocation(evaluation);
            return;
        }

        for (int index = 0; index < evaluation.Arguments.Length; index++)
        {
            ManagedExpressionValue argument = evaluation.Arguments[index];
            if (!argument.HasScalar ||
                argument.Scalar is not string text ||
                evaluation.RuntimeArguments[index] != 0)
            {
                continue;
            }

            nint evaluation2 = 0;
            try
            {
                evaluation2 = ComAbi.QueryInterface(
                    evaluation.Pointer,
                    ICorDebugEval2Abi.InterfaceId);
                fixed (char* textAddress = text)
                {
                    ThrowIfFunctionEvaluationUnavailable(
                        new ICorDebugEval2Abi(evaluation2).NewStringWithLength(
                            (nint)textAddress,
                            checked((uint)text.Length)),
                        "ICorDebugEval2.NewStringWithLength");
                }

                evaluation.PendingStringArgumentIndex = index;
                return;
            }
            finally
            {
                if (evaluation2 != 0)
                {
                    _ = ComAbi.Release(evaluation2);
                }
            }
        }

        for (int index = 0; index < evaluation.Arguments.Length; index++)
        {
            ManagedExpressionValue argument = evaluation.Arguments[index];
            bool materializeNullable = argument.RequiresNullableMaterialization &&
                !evaluation.RuntimeArgumentIsHeapHandle[index];
            bool materializeBox = argument.RequiresBoxing &&
                !evaluation.RuntimeArgumentIsHeapHandle[index];
            bool materializeOtherValue = !argument.BoxesNullableAsNull &&
                evaluation.RuntimeArguments[index] == 0 &&
                (argument.IsZeroValueTypeDefault || argument.Scalar is decimal or DateTime);
            if (materializeNullable || materializeBox || materializeOtherValue)
            {
                ScheduleStructuredArgumentAllocation(evaluation, index);
                return;
            }
        }

        for (int index = 0; index < evaluation.Arguments.Length; index++)
        {
            if (evaluation.Arguments[index].UserDefinedConversion is not null)
            {
                ScheduleUserDefinedConversion(evaluation, index);
                return;
            }
        }

        var temporaryArguments = new List<nint>();
        nint temporaryReceiver = 0;
        try
        {
            int receiverCount = evaluation.Receiver == 0 || evaluation.SuppressReceiver ? 0 : 1;
            nint[] arguments = new nint[checked(evaluation.Arguments.Length + receiverCount)];
            if (receiverCount != 0)
            {
                if (evaluation.ReceiverIsHeapHandle &&
                    evaluation.ReceiverValue?.DeclaredType is { ElementType: 0x11 })
                {
                    if (!TryDereferenceAndUnboxValue(evaluation.Receiver, out temporaryReceiver))
                    {
                        throw new InvalidOperationException(
                            "The value-type receiver has no materialized storage.");
                    }

                    arguments[0] = temporaryReceiver;
                }
                else
                {
                    arguments[0] = evaluation.Receiver;
                }
            }

            for (int index = 0; index < evaluation.Arguments.Length; index++)
            {
                ManagedUserDefinedConversion? explicitConversion =
                    evaluation.ExplicitUserDefinedConversion;
                ManagedUserDefinedOperator? userDefinedOperator =
                    evaluation.UserDefinedOperator;
                arguments[index + receiverCount] = userDefinedOperator is { IsLifted: true } &&
                    RequiresLiftedOperatorExtraction(
                        evaluation.Arguments[index], evaluation.Thread)
                    ? CreateLiftedOperatorArgument(
                        evaluation.Arguments[index],
                        userDefinedOperator,
                        index,
                        evaluation.RuntimeArguments[index],
                        evaluation.Thread,
                        temporaryArguments)
                    : index == 0 &&
                    explicitConversion is not null &&
                    RequiresNullableSourceExtraction(
                        evaluation.Arguments[index], explicitConversion, evaluation.Thread)
                        ? CreateNullableSourceConversionArgument(
                            evaluation.Pointer,
                            evaluation.Arguments[index],
                            explicitConversion,
                            evaluation.RuntimeArguments[index],
                            evaluation.Thread,
                            temporaryArguments)
                        : CreateFunctionArgument(
                            evaluation.Pointer,
                            evaluation.Arguments[index],
                            evaluation.RuntimeArguments[index],
                            temporaryArguments);
            }

            int callResult;
            nint evaluation2 = 0;
            try
            {
                nint emptyArgument = 0;
                fixed (nint* argumentsAddress = arguments)
                fixed (nint* typeArgumentsAddress = evaluation.TypeArguments)
                {
                    nint argumentListAddress = arguments.Length == 0
                        ? (nint)(&emptyArgument)
                        : (nint)argumentsAddress;
                    if (evaluation.TypeArguments.Length != 0)
                    {
                        evaluation2 = ComAbi.QueryInterface(
                            evaluation.Pointer,
                            ICorDebugEval2Abi.InterfaceId);
                        var api = new ICorDebugEval2Abi(evaluation2);
                        callResult = evaluation.ConstructsObject
                            ? api.NewParameterizedObject(
                                evaluation.Function,
                                checked((uint)evaluation.TypeArguments.Length),
                                (nint)typeArgumentsAddress,
                                checked((uint)arguments.Length),
                                argumentListAddress)
                            : api.CallParameterizedFunction(
                                evaluation.Function,
                                checked((uint)evaluation.TypeArguments.Length),
                                (nint)typeArgumentsAddress,
                                checked((uint)arguments.Length),
                                argumentListAddress);
                    }
                    else
                    {
                        var api = new ICorDebugEvalAbi(evaluation.Pointer);
                        callResult = evaluation.ConstructsObject
                            ? api.NewObject(
                                evaluation.Function,
                                checked((uint)arguments.Length),
                                argumentListAddress)
                            : api.CallFunction(
                                evaluation.Function,
                                checked((uint)arguments.Length),
                                argumentListAddress);
                    }
                }
            }
            finally
            {
                if (evaluation2 != 0)
                {
                    _ = ComAbi.Release(evaluation2);
                }
            }

            ThrowIfFunctionEvaluationUnavailable(
                callResult,
                evaluation.TypeArguments.Length != 0
                    ? evaluation.ConstructsObject
                        ? "ICorDebugEval2.NewParameterizedObject"
                        : "ICorDebugEval2.CallParameterizedFunction"
                    : evaluation.ConstructsObject
                    ? "ICorDebugEval.NewObject"
                    : "ICorDebugEval.CallFunction");
            evaluation.PendingStringArgumentIndex = -1;
            evaluation.PendingStructuredArgumentIndex = -1;
            evaluation.MethodCallScheduled = true;
        }
        finally
        {
            if (temporaryReceiver != 0)
            {
                _ = ComAbi.Release(temporaryReceiver);
            }

            foreach (nint temporaryArgument in temporaryArguments)
            {
                _ = ComAbi.Release(temporaryArgument);
            }
        }
    }

    private unsafe void ContinueAfterStringMaterialization(
        ManagedFunctionEvaluation active)
    {
        int argumentIndex = active.PendingStringArgumentIndex;
        if (argumentIndex < 0 || argumentIndex >= active.RuntimeArguments.Length)
        {
            throw new InvalidOperationException(
                "CoreCLR completed an unexpected function-evaluation stage.");
        }

        nint value = 0;
        nint handle = 0;
        nint nextEvaluation = 0;
        nint completedEvaluation = active.Pointer;
        try
        {
            nint* valueAddress = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(completedEvaluation).GetResult((nint)valueAddress),
                "ICorDebugEval.GetResult");
            value = RequirePointer(
                Volatile.Read(ref *valueAddress),
                "ICorDebugEval.GetResult");
            handle = ComAbi.QueryInterface(value, ICorDebugHandleValueAbi.InterfaceId);
            _ = ComAbi.Release(value);
            value = 0;

            nextEvaluation = CreateEvaluation(active.Thread);
            active.RuntimeArguments[argumentIndex] = handle;
            active.RuntimeArgumentIsHeapHandle[argumentIndex] = true;
            handle = 0;
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingStringArgumentIndex = -1;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after scheduling managed " +
                "function evaluation. The target's evaluation state is uncertain; this " +
                "debugger session must be disconnected.");
        }
        finally
        {
            if (completedEvaluation != 0)
            {
                _ = ComAbi.Release(completedEvaluation);
            }

            if (nextEvaluation != 0)
            {
                _ = ComAbi.Release(nextEvaluation);
            }

            if (handle != 0)
            {
                ReleaseFunctionEvaluationHandle(handle);
            }

            if (value != 0)
            {
                _ = ComAbi.Release(value);
            }
        }
    }

    private void ContinueFunctionEvaluation(string unsafeStateReason)
    {
        try
        {
            ContinueForFunctionEvaluation();
        }
        catch
        {
            _functionEvaluationDisabledReason = unsafeStateReason;
            _managedCallback.ThrowIfRuntimeFailed();
            throw;
        }
    }

    private void ReleaseFunctionEvaluationResources(
        ManagedFunctionEvaluation evaluation,
        bool runtimeAvailable = true)
    {
        if (evaluation.DebuggerTypeProxy is ManagedDebuggerTypeProxyEvaluation proxy)
        {
            ReleaseDebuggerTypeProxyPropertyResources(proxy, runtimeAvailable);
        }

        nint retainedEnumerable = evaluation.ResultsView?.RetainedEnumerableValue ?? 0;
        if (evaluation.Receiver != retainedEnumerable)
        {
            ReleaseFunctionEvaluationArgument(
                evaluation.Receiver, evaluation.ReceiverIsHeapHandle, runtimeAvailable);
        }

        for (int index = 0; index < evaluation.RuntimeArguments.Length; index++)
        {
            nint argument = evaluation.RuntimeArguments[index];
            if (argument != retainedEnumerable)
            {
                ReleaseFunctionEvaluationArgument(
                    argument, evaluation.RuntimeArgumentIsHeapHandle[index], runtimeAvailable);
            }
        }

        ReleaseFunctionEvaluationHandle(
            evaluation.ResultsView?.DetachReceiverIdentity()?.DetachHeapHandle() ?? 0,
            runtimeAvailable);
        evaluation.ResultsView?.Release();

        if (evaluation.Function != 0)
        {
            _ = ComAbi.Release(evaluation.Function);
        }

        foreach (nint typeArgument in evaluation.TypeArguments.Where(
            static typeArgument => typeArgument != 0))
        {
            _ = ComAbi.Release(typeArgument);
        }

        ReleaseFunctionEvaluationPointer(evaluation.PendingUserDefinedConversionFunction);
        foreach (nint typeArgument in evaluation.PendingUserDefinedConversionTypeArguments.Where(
            static typeArgument => typeArgument != 0))
        {
            _ = ComAbi.Release(typeArgument);
        }

        if (evaluation.Thread != 0)
        {
            _ = ComAbi.Release(evaluation.Thread);
        }

        if (evaluation.Pointer != 0)
        {
            _ = ComAbi.Release(evaluation.Pointer);
        }
    }
}
