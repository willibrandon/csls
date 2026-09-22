using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Executes exact implicit conversion operators as supervised function-evaluation stages.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedExpressionValue PrepareUserDefinedConversionArgument(
        ManagedExpressionValue value,
        ManagedBoundType sourceType,
        ManagedUserDefinedConversion conversion,
        ManagedReferenceConversion referenceConversions,
        nint thread)
    {
        ManagedExpressionValue prepared;
        if (sourceType.IsSameType(conversion.ParameterType) ||
            referenceConversions.IsImplicit(sourceType, conversion.ParameterType, thread))
        {
            prepared = value with { DeclaredType = conversion.ParameterType };
        }
        else if (referenceConversions.IsImplicitBoxing(
            sourceType, conversion.ParameterType, thread))
        {
            bool boxesNullable = _boundTypes.IsCoreType(
                sourceType, "System.Nullable`1", thread);
            bool boxesNullableAsNull = boxesNullable &&
                IsNullableBoxingEmpty(value, sourceType, thread);
            prepared = value with
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
        else if (ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
            sourceType, conversion.ParameterType, conversion.Language))
        {
            prepared = ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                value, sourceType, conversion.ParameterType, conversion.Language) with
            {
                DeclaredType = conversion.ParameterType
            };
        }
        else
        {
            throw new InvalidOperationException(
                $"The implicit conversion operator cannot receive " +
                $"'{sourceType.DisplayName}' as '{conversion.ParameterType.DisplayName}'.");
        }

        return prepared with { UserDefinedConversion = conversion };
    }

    private unsafe void ScheduleUserDefinedConversion(
        ManagedFunctionEvaluation evaluation,
        int index)
    {
        ManagedExpressionValue argument = evaluation.Arguments[index];
        ManagedUserDefinedConversion conversion = argument.UserDefinedConversion ??
            throw new InvalidOperationException("A conversion stage has no selected operator.");
        CorDebugLoadedModule module = _boundTypes.GetModule(conversion.DeclaringType);
        nint function = 0;
        nint[] typeArguments = [];
        var temporaryArguments = new List<nint>();
        try
        {
            function = GetModuleFunction(module.Pointer, conversion.MethodToken);
            typeArguments = ManagedRuntimeTypeArguments.ResolveBound(
                conversion.DeclaringType.TypeArguments, _boundTypes, evaluation.Thread);
            nint source = CreateFunctionArgument(
                evaluation.Pointer,
                argument,
                evaluation.RuntimeArguments[index],
                temporaryArguments);
            int result;
            nint evaluation2 = 0;
            try
            {
                if (typeArguments.Length == 0)
                {
                    result = new ICorDebugEvalAbi(evaluation.Pointer).CallFunction(
                        function, 1, (nint)(&source));
                }
                else
                {
                    evaluation2 = ComAbi.QueryInterface(
                        evaluation.Pointer, ICorDebugEval2Abi.InterfaceId);
                    fixed (nint* typeArgumentsAddress = typeArguments)
                    {
                        result = new ICorDebugEval2Abi(evaluation2).CallParameterizedFunction(
                            function,
                            checked((uint)typeArguments.Length),
                            (nint)typeArgumentsAddress,
                            1,
                            (nint)(&source));
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
                result,
                typeArguments.Length == 0
                    ? "ICorDebugEval.CallFunction"
                    : "ICorDebugEval2.CallParameterizedFunction");
            evaluation.PendingUserDefinedConversionFunction = function;
            evaluation.PendingUserDefinedConversionTypeArguments = typeArguments;
            evaluation.PendingUserDefinedConversionArgumentIndex = index;
            function = 0;
            typeArguments = [];
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(function);
            foreach (nint typeArgument in typeArguments)
            {
                ReleaseFunctionEvaluationPointer(typeArgument);
            }

            foreach (nint temporaryArgument in temporaryArguments)
            {
                ReleaseFunctionEvaluationPointer(temporaryArgument);
            }
        }
    }

    private unsafe void ContinueAfterUserDefinedConversion(ManagedFunctionEvaluation active)
    {
        int index = active.PendingUserDefinedConversionArgumentIndex;
        if (index < 0 || index >= active.Arguments.Length)
        {
            throw new InvalidOperationException("CoreCLR completed an unexpected conversion stage.");
        }

        ManagedUserDefinedConversion conversion = active.Arguments[index].UserDefinedConversion ??
            throw new InvalidOperationException("The completed conversion has no selected operator.");
        nint completedEvaluation = active.Pointer;
        nint value = 0;
        nint retained = 0;
        bool retainedIsHeapHandle = false;
        nint nextEvaluation = 0;
        nint oldArgument = active.RuntimeArguments[index];
        bool oldArgumentIsHeapHandle = active.RuntimeArgumentIsHeapHandle[index];
        try
        {
            nint* valueAddress = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(completedEvaluation).GetResult((nint)valueAddress),
                "ICorDebugEval.GetResult");
            value = RequirePointer(Volatile.Read(ref *valueAddress), "ICorDebugEval.GetResult");

            ManagedExpressionValue converted;
            if (conversion.ResultType.IsReference)
            {
                if (TryDereferenceValue(value, out nint dereferenced))
                {
                    _ = ComAbi.Release(dereferenced);
                    retained = CreateFunctionEvaluationHandle(value);
                    retainedIsHeapHandle = true;
                    converted = CreateMaterializedUserDefinedConversionValue(
                        conversion.ResultType);
                }
                else
                {
                    converted = ManagedExpressionValueFactory.FromScalar(
                        value: null, conversion.ResultType.DisplayName) with
                    {
                        DeclaredType = conversion.ResultType
                    };
                }
            }
            else if (conversion.ResultType.ElementType == 0x11)
            {
                _ = ComAbi.AddRef(value);
                retained = value;
                converted = CreateMaterializedUserDefinedConversionValue(
                    conversion.ResultType);
            }
            else
            {
                ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
                converted = ManagedExpressionValueFactory.FromVariable(
                    new DebugVariableInfo(
                        "$conversion",
                        display.Value,
                        display.Type,
                        VariablesReference: 0,
                        MemoryReference: null,
                        EvaluateName: null),
                    runtimeValueReference: 0,
                    display) with
                {
                    DeclaredType = conversion.ResultType
                };
                if (!converted.HasScalar)
                {
                    throw new InvalidOperationException(
                        $"The conversion result '{conversion.ResultType.DisplayName}' cannot be materialized.");
                }
            }

            converted = ApplyUserDefinedConversionTarget(
                converted, conversion, active.Thread);

            nextEvaluation = CreateEvaluation(active.Thread);
            active.Arguments[index] = converted;
            active.RuntimeArguments[index] = retained;
            active.RuntimeArgumentIsHeapHandle[index] = retainedIsHeapHandle;
            retained = 0;
            ReleaseFunctionEvaluationArgument(oldArgument, oldArgumentIsHeapHandle);
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingUserDefinedConversionArgumentIndex = -1;
            ReleasePendingUserDefinedConversion(active);
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleNextFunctionEvaluationStage(active);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after applying an implicit conversion. " +
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

            ReleaseFunctionEvaluationArgument(retained, retainedIsHeapHandle);
            if (value != 0)
            {
                _ = ComAbi.Release(value);
            }
        }
    }

    private static void ReleasePendingUserDefinedConversion(ManagedFunctionEvaluation evaluation)
    {
        ReleaseFunctionEvaluationPointer(evaluation.PendingUserDefinedConversionFunction);
        evaluation.PendingUserDefinedConversionFunction = 0;
        foreach (nint typeArgument in evaluation.PendingUserDefinedConversionTypeArguments)
        {
            ReleaseFunctionEvaluationPointer(typeArgument);
        }

        evaluation.PendingUserDefinedConversionTypeArguments = [];
    }

    private static ManagedExpressionValue CreateMaterializedUserDefinedConversionValue(
        ManagedBoundType type) => new(
            new DebugVariableInfo(
                "$conversion",
                "{...}",
                type.DisplayName,
                VariablesReference: 0,
                MemoryReference: null,
                EvaluateName: null),
            Scalar: null,
            HasScalar: false,
            Type: type.DisplayName,
            RuntimeValueReference: 0,
            DeclaredType: type,
            IsMaterializedFunctionArgument: true);

    private ManagedExpressionValue ApplyUserDefinedConversionTarget(
        ManagedExpressionValue value,
        ManagedUserDefinedConversion conversion,
        nint thread)
    {
        if (conversion.ResultType.IsSameType(conversion.TargetType))
        {
            return value with { DeclaredType = conversion.TargetType };
        }

        var referenceConversions = new ManagedReferenceConversion(_boundTypes);
        if (referenceConversions.IsImplicit(
            conversion.ResultType, conversion.TargetType, thread))
        {
            return value with { DeclaredType = conversion.TargetType };
        }

        if (ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
            conversion.ResultType, conversion.TargetType, conversion.Language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                value, conversion.ResultType, conversion.TargetType, conversion.Language) with
            {
                DeclaredType = conversion.TargetType
            };
        }

        throw new InvalidOperationException(
            $"The implicit conversion result '{conversion.ResultType.DisplayName}' cannot flow to " +
            $"'{conversion.TargetType.DisplayName}'.");
    }
}
