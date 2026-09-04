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

        var temporaryArguments = new List<nint>();
        try
        {
            int receiverCount = evaluation.Receiver == 0 ? 0 : 1;
            nint[] arguments = new nint[checked(evaluation.Arguments.Length + receiverCount)];
            if (receiverCount != 0)
            {
                arguments[0] = evaluation.Receiver;
            }

            for (int index = 0; index < evaluation.Arguments.Length; index++)
            {
                arguments[index + receiverCount] = CreateFunctionArgument(
                    evaluation.Pointer,
                    evaluation.Arguments[index],
                    evaluation.RuntimeArguments[index],
                    temporaryArguments);
            }

            int callResult;
            fixed (nint* argumentsAddress = arguments)
            {
                var api = new ICorDebugEvalAbi(evaluation.Pointer);
                callResult = evaluation.ConstructsObject
                    ? api.NewObject(
                        evaluation.Function,
                        checked((uint)arguments.Length),
                        (nint)argumentsAddress)
                    : api.CallFunction(
                        evaluation.Function,
                        checked((uint)arguments.Length),
                        (nint)argumentsAddress);
            }

            ThrowIfFunctionEvaluationUnavailable(
                callResult,
                evaluation.ConstructsObject
                    ? "ICorDebugEval.NewObject"
                    : "ICorDebugEval.CallFunction");
            evaluation.PendingStringArgumentIndex = -1;
            evaluation.MethodCallScheduled = true;
        }
        finally
        {
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
            handle = 0;
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingStringArgumentIndex = -1;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleNextFunctionEvaluationStage(active);
            int continueResult = new ICorDebugControllerAbi(_debugProcess)
                .Continue(fIsOutOfBand: 0);
            if (continueResult < 0)
            {
                _functionEvaluationDisabledReason =
                    "The debugger could not resume the target after scheduling managed " +
                    "function evaluation. The target's evaluation state is uncertain; this " +
                    "debugger session must be disconnected.";
                CorDebugHResult.ThrowIfFailed(
                    continueResult,
                    "ICorDebugController.Continue");
            }
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

    private static void ReleaseFunctionEvaluationResources(
        ManagedFunctionEvaluation evaluation)
    {
        ReleaseFunctionEvaluationHandle(evaluation.Receiver);
        foreach (nint argument in evaluation.RuntimeArguments)
        {
            ReleaseFunctionEvaluationHandle(argument);
        }

        if (evaluation.Function != 0)
        {
            _ = ComAbi.Release(evaluation.Function);
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
