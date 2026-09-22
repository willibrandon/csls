using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Materializes loaded framework value-type arguments for managed calls.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private void ScheduleStructuredArgumentAllocation(ManagedFunctionEvaluation evaluation, int index)
    {
        ManagedExpressionValue argument = evaluation.Arguments[index];
        nint thread = evaluation.Thread;
        ManagedBoundType declaredType = argument.DeclaredType ?? throw new InvalidOperationException(
            "A structured argument has no exact declared type.");
        string expectedType = argument.Scalar is decimal ? "System.Decimal" : "System.DateTime";
        if (!_boundTypes.IsCoreType(declaredType, expectedType, thread))
        {
            throw new InvalidOperationException("A structured argument has an invalid runtime type.");
        }

        nint runtimeType = 0;
        nint runtimeClass = 0;
        nint evaluation2 = 0;
        try
        {
            runtimeType = _boundTypes.ResolveRuntimeType(declaredType, thread);
            runtimeClass = GetRuntimeTypeClass(runtimeType);
            evaluation2 = ComAbi.QueryInterface(evaluation.Pointer, ICorDebugEval2Abi.InterfaceId);
            ThrowIfFunctionEvaluationUnavailable(
                new ICorDebugEval2Abi(evaluation2).NewParameterizedObjectNoConstructor(
                    runtimeClass, nTypeArgs: 0, ppTypeArgs: 0),
                "ICorDebugEval2.NewParameterizedObjectNoConstructor");
            evaluation.PendingStructuredArgumentIndex = index;
        }
        finally
        {
            if (evaluation2 != 0)
            {
                _ = ComAbi.Release(evaluation2);
            }

            if (runtimeType != 0)
            {
                _ = ComAbi.Release(runtimeType);
            }

            if (runtimeClass != 0)
            {
                _ = ComAbi.Release(runtimeClass);
            }
        }
    }

    private unsafe void ContinueAfterStructuredArgumentAllocation(ManagedFunctionEvaluation active)
    {
        int index = active.PendingStructuredArgumentIndex;
        if (index < 0 || index >= active.RuntimeArguments.Length)
        {
            throw new InvalidOperationException("CoreCLR completed an unexpected value-type allocation.");
        }

        nint completedEvaluation = active.Pointer;
        nint value = 0;
        nint unboxed = 0;
        nint runtimeType = 0;
        nint handle = 0;
        nint nextEvaluation = 0;
        try
        {
            nint* address = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(completedEvaluation).GetResult((nint)address),
                "ICorDebugEval.GetResult");
            value = RequirePointer(Volatile.Read(ref *address), "ICorDebugEval.GetResult");
            if (!TryDereferenceAndUnboxValue(value, out unboxed))
            {
                throw new InvalidOperationException("CoreCLR did not allocate the structured value type.");
            }

            ManagedExpressionValue argument = active.Arguments[index];
            ManagedBoundType declaredType = argument.DeclaredType ?? throw new InvalidOperationException(
                "A structured argument has no exact declared type.");
            runtimeType = _boundTypes.ResolveRuntimeType(declaredType, active.Thread);
            if (argument.Scalar is decimal amount)
            {
                SetDecimalArgument(unboxed, runtimeType, amount);
            }
            else if (argument.Scalar is DateTime date)
            {
                SetDateTimeArgument(unboxed, runtimeType, date);
            }

            handle = CreateFunctionEvaluationHandle(value);
            nextEvaluation = CreateEvaluation(active.Thread);
            active.RuntimeArguments[index] = handle;
            handle = 0;
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingStructuredArgumentIndex = -1;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after allocating a structured value type. " +
                "The target's evaluation state is uncertain; this debugger session must be disconnected.");
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

            if (runtimeType != 0)
            {
                _ = ComAbi.Release(runtimeType);
            }

            if (unboxed != 0)
            {
                _ = ComAbi.Release(unboxed);
            }

            if (value != 0)
            {
                _ = ComAbi.Release(value);
            }
        }
    }

    private void SetDecimalArgument(nint value, nint runtimeType, decimal amount)
    {
        int[] bits = decimal.GetBits(amount);
        uint low = unchecked((uint)bits[0]);
        uint middle = unchecked((uint)bits[1]);
        uint high = unchecked((uint)bits[2]);
        uint flags = unchecked((uint)bits[3]);
        int found = 0;
        VisitDeclaredRuntimeFields(value, runtimeType, (name, field) =>
        {
            switch (name)
            {
                case "flags" or "_flags":
                    SetStructuredField(field, flags, sizeof(uint));
                    found |= 1;
                    break;
                case "hi" or "_hi32":
                    SetStructuredField(field, high, sizeof(uint));
                    found |= 2;
                    break;
                case "mid":
                    SetStructuredField(field, middle, sizeof(uint));
                    found |= 4;
                    break;
                case "lo":
                    SetStructuredField(field, low, sizeof(uint));
                    found |= 8;
                    break;
                case "_lo64":
                    SetStructuredField(field, low | ((ulong)middle << 32), sizeof(ulong));
                    found |= 12;
                    break;
            }
        });
        if (found != 15)
        {
            throw new InvalidOperationException("System.Decimal does not expose its required runtime fields.");
        }
    }

    private void SetDateTimeArgument(nint value, nint runtimeType, DateTime date)
    {
        bool found = false;
        VisitDeclaredRuntimeFields(value, runtimeType, (name, field) =>
        {
            if (name is "_dateData" or "dateData")
            {
                SetStructuredField(field, checked((ulong)date.Ticks), sizeof(ulong));
                found = true;
            }
        });
        if (!found)
        {
            throw new InvalidOperationException("System.DateTime does not expose its required date-data field.");
        }
    }

    private static unsafe void SetStructuredField(nint field, ulong bits, uint expectedSize)
    {
        uint size = 0;
        uint* sizeAddress = &size;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(field).GetSize((nint)sizeAddress),
            "ICorDebugValue.GetSize");
        if (Volatile.Read(ref *sizeAddress) != expectedSize)
        {
            throw new InvalidOperationException("A structured argument has an unexpected field size.");
        }

        nint generic = ComAbi.QueryInterface(field, ICorDebugGenericValueAbi.InterfaceId);
        try
        {
            if (expectedSize == sizeof(uint))
            {
                uint narrow = checked((uint)bits);
                SetGenericValue(generic, &narrow);
            }
            else
            {
                SetGenericValue(generic, &bits);
            }
        }
        finally
        {
            _ = ComAbi.Release(generic);
        }
    }
}
