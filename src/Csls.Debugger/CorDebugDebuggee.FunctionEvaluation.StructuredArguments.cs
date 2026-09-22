using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Materializes loaded framework value-type arguments for managed calls.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe void ScheduleStructuredArgumentAllocation(ManagedFunctionEvaluation evaluation, int index)
    {
        ManagedExpressionValue argument = evaluation.Arguments[index];
        nint thread = evaluation.Thread;
        ManagedBoundType declaredType = argument.DeclaredType ?? throw new InvalidOperationException(
            "A structured argument has no exact declared type.");
        if (argument.IsZeroValueTypeDefault)
        {
            if (declaredType.ElementType != 0x11 || _boundTypes.IsByRefLike(declaredType))
            {
                throw new InvalidOperationException("The optional value type cannot be boxed safely.");
            }
        }
        else if (argument.RequiresNullableMaterialization)
        {
            if (!_boundTypes.IsCoreType(declaredType, "System.Nullable`1", thread) ||
                declaredType.TypeArguments.Count != 1)
            {
                throw new InvalidOperationException("A nullable argument has an invalid runtime type.");
            }
        }
        else
        {
            string expectedType = argument.Scalar is decimal ? "System.Decimal" : "System.DateTime";
            if (!_boundTypes.IsCoreType(declaredType, expectedType, thread))
            {
                throw new InvalidOperationException("A structured argument has an invalid runtime type.");
            }
        }

        ScheduleStructuredValueAllocation(evaluation, declaredType);
        evaluation.PendingStructuredArgumentIndex = index;
    }

    private void ScheduleStructuredReceiverAllocation(ManagedFunctionEvaluation evaluation)
    {
        ManagedBoundType declaredType = evaluation.ReceiverValue?.DeclaredType ??
            throw new InvalidOperationException("A temporary receiver has no exact declared type.");
        if (declaredType.ElementType != 0x11 || _boundTypes.IsByRefLike(declaredType))
        {
            throw new InvalidOperationException("The temporary receiver cannot be materialized safely.");
        }

        ScheduleStructuredValueAllocation(evaluation, declaredType);
        evaluation.PendingStructuredReceiver = true;
    }

    private unsafe void ScheduleStructuredValueAllocation(
        ManagedFunctionEvaluation evaluation,
        ManagedBoundType declaredType)
    {
        if (declaredType.TypeArguments.Count > MaximumFunctionEvaluationArgumentCount)
        {
            throw new NotSupportedException("A structured value exceeds the supported generic arity.");
        }

        nint runtimeType = 0;
        nint runtimeClass = 0;
        nint evaluation2 = 0;
        nint[] typeArguments = new nint[declaredType.TypeArguments.Count];
        try
        {
            runtimeType = _boundTypes.ResolveRuntimeType(declaredType, evaluation.Thread);
            runtimeClass = GetRuntimeTypeClass(runtimeType);
            for (int argumentIndex = 0; argumentIndex < typeArguments.Length; argumentIndex++)
            {
                typeArguments[argumentIndex] = _boundTypes.ResolveRuntimeType(
                    declaredType.TypeArguments[argumentIndex], evaluation.Thread);
            }

            evaluation2 = ComAbi.QueryInterface(evaluation.Pointer, ICorDebugEval2Abi.InterfaceId);
            fixed (nint* argumentsAddress = typeArguments)
            {
                ThrowIfFunctionEvaluationUnavailable(
                    new ICorDebugEval2Abi(evaluation2).NewParameterizedObjectNoConstructor(
                        runtimeClass,
                        checked((uint)typeArguments.Length),
                        typeArguments.Length == 0 ? 0 : (nint)argumentsAddress),
                    "ICorDebugEval2.NewParameterizedObjectNoConstructor");
            }
        }
        finally
        {
            foreach (nint typeArgument in typeArguments.Where(static argument => argument != 0))
            {
                _ = ComAbi.Release(typeArgument);
            }

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
        nint sourceArgument = active.RuntimeArguments[index];
        bool sourceArgumentIsHeapHandle = active.RuntimeArgumentIsHeapHandle[index];
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
            else if (argument.RequiresNullableMaterialization && !argument.IsZeroValueTypeDefault)
            {
                SetNullableArgument(unboxed, runtimeType, argument, active.RuntimeArguments[index]);
            }

            handle = CreateFunctionEvaluationHandle(value);
            nextEvaluation = CreateEvaluation(active.Thread);
            active.RuntimeArguments[index] = handle;
            active.RuntimeArgumentIsHeapHandle[index] = true;
            handle = 0;
            ReleaseFunctionEvaluationArgument(sourceArgument, sourceArgumentIsHeapHandle);
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

    private unsafe void ContinueAfterStructuredReceiverAllocation(ManagedFunctionEvaluation active)
    {
        if (!active.PendingStructuredReceiver || active.Receiver != 0)
        {
            throw new InvalidOperationException("CoreCLR completed an unexpected receiver allocation.");
        }

        nint completedEvaluation = active.Pointer;
        nint value = 0;
        nint handle = 0;
        nint nextEvaluation = 0;
        try
        {
            nint* address = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(completedEvaluation).GetResult((nint)address),
                "ICorDebugEval.GetResult");
            value = RequirePointer(Volatile.Read(ref *address), "ICorDebugEval.GetResult");
            handle = CreateFunctionEvaluationHandle(value);
            nextEvaluation = CreateEvaluation(active.Thread);

            active.Receiver = handle;
            active.ReceiverIsHeapHandle = true;
            handle = 0;
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingStructuredReceiver = false;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after allocating a value-type receiver. " +
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

    private void SetNullableArgument(
        nint value,
        nint runtimeType,
        ManagedExpressionValue argument,
        nint sourceValue)
    {
        bool foundHasValue = false;
        bool foundValue = false;
        VisitDeclaredRuntimeFields(value, runtimeType, (name, field) =>
        {
            if (string.Equals(name, "hasValue", StringComparison.Ordinal))
            {
                SetManagedPrimitiveValue(field, "bool", true);
                foundHasValue = true;
                return;
            }

            if (!string.Equals(name, "value", StringComparison.Ordinal))
            {
                return;
            }

            if (argument.HasScalar)
            {
                SetManagedPrimitiveValue(field, argument.Type, argument.Scalar!);
            }
            else
            {
                if (sourceValue == 0 || !TryDereferenceAndUnboxValue(sourceValue, out nint source))
                {
                    throw new InvalidOperationException("A nullable argument has no retained underlying value.");
                }

                try
                {
                    using var assignment = ManagedValueTypeAssignment.Prepare(
                        field, source, OpenRuntimeModule);
                    assignment.Write();
                }
                finally
                {
                    _ = ComAbi.Release(source);
                }
            }

            foundValue = true;
        });
        if (!foundHasValue || !foundValue)
        {
            throw new InvalidOperationException("System.Nullable<T> does not expose its required runtime fields.");
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
