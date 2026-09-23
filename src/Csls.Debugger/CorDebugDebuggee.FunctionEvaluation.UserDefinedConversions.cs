using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Executes exact implicit conversion operators as supervised function-evaluation stages.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Determines whether an explicit conversion root selects target code.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier.</param>
    /// <param name="plan">The validated explicit conversion expression.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>True when a loaded conversion operator is selected.</returns>
    internal bool HasUserDefinedExplicitConversion(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(plan, frame.ExpressionLanguage);
        if (plan.Root is not
            {
                Kind: DebugExpressionNodeKind.Conversion,
                TypeName: not null,
                Children: [DebugExpressionNode operandNode]
            })
        {
            return false;
        }

        ManagedExpressionValue operand = EvaluateNode(frame, plan, operandNode, generation);
        nint thread = GetThread(frame.ThreadId);
        try
        {
            ManagedBoundType? source = BindFunctionEvaluationArgumentTypes(
                [operand], plan.Language, thread)[0];
            if (source is null)
            {
                return false;
            }

            ManagedBoundType target = BindConversionTarget(
                plan.Root.TypeName, plan.Language, thread);
            ManagedUserDefinedConversion? conversion = new ManagedUserDefinedConversionResolver(
                _boundTypes, thread, plan.Language).ResolveExplicit(source, target);
            return conversion is not null &&
                (!conversion.IsLifted || !IsNullableBoxingEmpty(operand, source, thread));
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(thread);
        }
    }

    private bool TryEvaluateEmptyLiftedExplicitConversion(
        ManagedExpressionValue operand,
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language,
        nint thread,
        out ManagedExpressionValue result)
    {
        ManagedUserDefinedConversion? conversion = new ManagedUserDefinedConversionResolver(
            _boundTypes, thread, language).ResolveExplicit(source, target);
        if (conversion is { IsLifted: true } && IsNullableBoxingEmpty(operand, source, thread))
        {
            result = CreateEmptyLiftedConversionValue(target);
            return true;
        }

        result = null!;
        return false;
    }

    private ManagedExpressionValue PrepareUserDefinedConversionArgument(
        ManagedExpressionValue value,
        ManagedBoundType sourceType,
        ManagedUserDefinedConversion conversion,
        ManagedReferenceConversion referenceConversions,
        nint thread)
    {
        if (conversion.IsLifted)
        {
            if (IsNullableBoxingEmpty(value, sourceType, thread))
            {
                return CreateEmptyLiftedConversionValue(conversion.TargetType);
            }

            return value with { UserDefinedConversion = conversion };
        }

        ManagedExpressionValue prepared = PrepareUserDefinedConversionInput(
            value,
            sourceType,
            conversion,
            referenceConversions,
            thread);

        return prepared with { UserDefinedConversion = conversion };
    }

    private ManagedExpressionValue PrepareUserDefinedConversionInput(
        ManagedExpressionValue value,
        ManagedBoundType sourceType,
        ManagedUserDefinedConversion conversion,
        ManagedReferenceConversion referenceConversions,
        nint thread)
    {
        if (sourceType.IsSameType(conversion.ParameterType) ||
            referenceConversions.IsImplicit(sourceType, conversion.ParameterType, thread))
        {
            return value with { DeclaredType = conversion.ParameterType };
        }

        if (sourceType.IsReference && conversion.ParameterType.IsReference &&
            referenceConversions.IsImplicit(conversion.ParameterType, sourceType, thread))
        {
            if (value is not { HasScalar: true, Scalar: null })
            {
                if (value.RuntimeValueReference <= 0)
                {
                    throw new InvalidOperationException(
                        "An explicit reference conversion has no retained runtime value.");
                }

                ManagedBoundType actual = _boundTypes.CaptureValue(
                    GetRuntimeValue(value), thread);
                if (!referenceConversions.IsRuntimeAssignable(
                    actual, conversion.ParameterType, thread))
                {
                    throw new InvalidOperationException(
                        $"The runtime value of type '{actual.DisplayName}' cannot be cast to " +
                        $"'{conversion.ParameterType.DisplayName}'.");
                }
            }

            return value with
            {
                DeclaredType = conversion.ParameterType,
                ExplicitReceiverType = conversion.ParameterType
            };
        }

        if (sourceType.IsReference && !conversion.ParameterType.IsReference &&
            !_boundTypes.IsCoreType(conversion.ParameterType, "System.Nullable`1", thread) &&
            referenceConversions.IsImplicitBoxing(
                conversion.ParameterType, sourceType, thread))
        {
            if (value is { HasScalar: true, Scalar: null } ||
                value.RuntimeValueReference <= 0)
            {
                throw new InvalidOperationException(
                    $"A null reference cannot be unboxed to " +
                    $"'{conversion.ParameterType.DisplayName}'.");
            }

            ManagedBoundType actual = _boundTypes.CaptureValue(
                GetRuntimeValue(value), thread);
            if (!actual.IsSameType(conversion.ParameterType))
            {
                throw new InvalidOperationException(
                    $"The boxed runtime value has type '{actual.DisplayName}', which cannot be " +
                        $"unboxed to '{conversion.ParameterType.DisplayName}'.");
            }

            bool requiresValueStorage = conversion.ParameterType.ElementType == 0x11;
            if (!requiresValueStorage && !value.HasScalar)
            {
                throw new InvalidOperationException(
                    $"The boxed primitive '{conversion.ParameterType.DisplayName}' cannot be decoded.");
            }

            return value with
            {
                DeclaredType = conversion.ParameterType,
                ExplicitReceiverType = conversion.ParameterType,
                RequiresUnboxing = requiresValueStorage
            };
        }

        if (referenceConversions.IsImplicitBoxing(
            sourceType, conversion.ParameterType, thread))
        {
            bool boxesNullable = _boundTypes.IsCoreType(
                sourceType, "System.Nullable`1", thread);
            bool boxesNullableAsNull = boxesNullable &&
                IsNullableBoxingEmpty(value, sourceType, thread);
            return value with
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

        if (ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
            sourceType, conversion.ParameterType, conversion.Language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                value, sourceType, conversion.ParameterType, conversion.Language) with
            {
                DeclaredType = conversion.ParameterType
            };
        }

        if (ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
            sourceType, conversion.ParameterType, conversion.Language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertStandardExplicitUserDefinedConversion(
                value, sourceType, conversion.ParameterType, conversion.Language) with
            {
                DeclaredType = conversion.ParameterType
            };
        }

        throw new InvalidOperationException(
            $"The conversion operator cannot receive " +
            $"'{sourceType.DisplayName}' as '{conversion.ParameterType.DisplayName}'.");
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
            nint source = conversion.IsLifted
                ? CreateLiftedUserDefinedConversionArgument(
                    argument,
                    conversion,
                    evaluation.RuntimeArguments[index],
                    evaluation.Thread,
                    temporaryArguments)
                : CreateFunctionArgument(
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

    private nint CreateLiftedUserDefinedConversionArgument(
        ManagedExpressionValue argument,
        ManagedUserDefinedConversion conversion,
        nint runtimeArgument,
        nint thread,
        List<nint> temporaryArguments)
    {
        if (runtimeArgument == 0 || argument.DeclaredType is not ManagedBoundType nullableType)
        {
            throw new InvalidOperationException(
                "A populated lifted conversion has no retained nullable source value.");
        }

        nint nullableValue = 0;
        nint runtimeType = 0;
        nint containedValue = 0;
        try
        {
            if (!TryDereferenceAndUnboxValue(runtimeArgument, out nullableValue))
            {
                throw new InvalidOperationException(
                    "A populated lifted conversion has no nullable source storage.");
            }

            runtimeType = _boundTypes.ResolveRuntimeType(nullableType, thread);
            VisitDeclaredRuntimeFields(nullableValue, runtimeType, (name, field) =>
            {
                if (!string.Equals(name, "value", StringComparison.Ordinal))
                {
                    return;
                }

                ManagedBoundType actual = _boundTypes.CaptureValue(field, thread);
                if (!actual.IsSameType(conversion.ParameterType))
                {
                    throw new InvalidOperationException(
                        "A lifted conversion's contained value does not match its operator parameter.");
                }

                _ = ComAbi.AddRef(field);
                containedValue = field;
            });
            if (containedValue == 0)
            {
                throw new InvalidOperationException(
                    "System.Nullable<T> does not expose its required value field.");
            }

            temporaryArguments.Add(containedValue);
            nint result = containedValue;
            containedValue = 0;
            return result;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(containedValue);
            ReleaseFunctionEvaluationPointer(runtimeType);
            ReleaseFunctionEvaluationPointer(nullableValue);
        }
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
        if (conversion.IsLifted &&
            _boundTypes.IsCoreType(conversion.TargetType, "System.Nullable`1", thread))
        {
            return value with
            {
                DeclaredType = conversion.TargetType,
                IsNullableValue = true,
                RequiresNullableMaterialization = true,
                IsMaterializedFunctionArgument = false
            };
        }

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

        if (ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
            conversion.ResultType, conversion.TargetType, conversion.Language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertStandardExplicitUserDefinedConversion(
                value, conversion.ResultType, conversion.TargetType, conversion.Language) with
            {
                DeclaredType = conversion.TargetType
            };
        }

        throw new InvalidOperationException(
            $"The conversion result '{conversion.ResultType.DisplayName}' cannot flow to " +
            $"'{conversion.TargetType.DisplayName}'.");
    }

    private static ManagedExpressionValue CreateEmptyLiftedConversionValue(
        ManagedBoundType target)
    {
        if (target.IsReference)
        {
            return ManagedExpressionValueFactory.FromScalar(
                value: null, target.DisplayName) with
            {
                DeclaredType = target
            };
        }

        ManagedExpressionValue empty =
            ManagedExpressionValueFactory.FromZeroValueTypeDefault(target);
        return empty with
        {
            Display = empty.Display with { Value = "null" },
            IsNullableValue = true
        };
    }

    private bool TryCreateExplicitUserDefinedConversionResult(
        nint value,
        ManagedFunctionEvaluation evaluation,
        DebugStopGeneration generation,
        out ManagedFunctionEvaluationResult result)
    {
        ManagedUserDefinedConversion? conversion = evaluation.ExplicitUserDefinedConversion;
        if (conversion is null)
        {
            result = null!;
            return false;
        }

        ValidateExplicitUserDefinedReferenceResult(value, conversion, evaluation.Thread);
        if (conversion.ResultType.IsSameType(conversion.TargetType) ||
            !ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                conversion.ResultType, conversion.TargetType, conversion.Language))
        {
            result = null!;
            return false;
        }

        ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
        ManagedExpressionValue operatorResult = ManagedExpressionValueFactory.FromVariable(
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
        ManagedExpressionValue converted = ApplyUserDefinedConversionTarget(
            operatorResult, conversion, evaluation.Thread);
        result = new ManagedFunctionEvaluationResult(
            converted.ToResult() with { TargetCodeExecuted = true },
            RuntimeValueReference: 0,
            generation,
            DebuggerTypeProxyApplied: false,
            DeclaredType: conversion.TargetType);
        return true;
    }

    private void ValidateExplicitUserDefinedReferenceResult(
        nint value,
        ManagedUserDefinedConversion conversion,
        nint thread)
    {
        if (!conversion.ResultType.IsReference || !conversion.TargetType.IsReference ||
            conversion.ResultType.IsSameType(conversion.TargetType))
        {
            return;
        }

        var referenceConversions = new ManagedReferenceConversion(_boundTypes);
        if (referenceConversions.IsImplicit(
            conversion.ResultType, conversion.TargetType, thread))
        {
            return;
        }

        nint dereferenced = 0;
        try
        {
            if (!TryDereferenceValue(value, out dereferenced))
            {
                return;
            }

            ManagedBoundType actual = _boundTypes.CaptureValue(dereferenced, thread);
            if (!referenceConversions.IsRuntimeAssignable(
                actual, conversion.TargetType, thread))
            {
                throw new InvalidOperationException(
                    $"The conversion result has runtime type '{actual.DisplayName}', which cannot be " +
                    $"cast to '{conversion.TargetType.DisplayName}'.");
            }
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(dereferenced);
        }
    }

    private ManagedFunctionBinding ResolveUserDefinedExplicitConversion(
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language,
        nint thread,
        out ManagedUserDefinedConversion conversion)
    {
        conversion =
            new ManagedUserDefinedConversionResolver(
                _boundTypes, thread, language).ResolveExplicit(source, target) ??
            throw new InvalidOperationException(
                $"No loaded user-defined conversion exists from " +
                $"'{source.DisplayName}' to '{target.DisplayName}'.");
        CorDebugLoadedModule module = _boundTypes.GetModule(conversion.DeclaringType);
        nint function = 0;
        nint[] typeArguments = [];
        try
        {
            function = GetModuleFunction(module.Pointer, conversion.MethodToken);
            typeArguments = ManagedRuntimeTypeArguments.ResolveBound(
                conversion.DeclaringType.TypeArguments, _boundTypes, thread);
            var binding = new ManagedFunctionBinding(
                function,
                typeArguments,
                target,
                [conversion.ParameterType],
                [0],
                [null]);
            function = 0;
            typeArguments = [];
            return binding;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(function);
            foreach (nint typeArgument in typeArguments)
            {
                ReleaseFunctionEvaluationPointer(typeArgument);
            }
        }
    }

    private ManagedBoundType BindConversionTarget(
        string typeName,
        DebugExpressionLanguage language,
        nint thread)
    {
        string? primitive = ManagedPrimitiveConversionEvaluator.TryNormalizeTypeName(
            typeName, language);
        return _boundTypes.BindName(
            primitive ?? typeName,
            primitive is null ? language : DebugExpressionLanguage.CSharp,
            thread);
    }
}
