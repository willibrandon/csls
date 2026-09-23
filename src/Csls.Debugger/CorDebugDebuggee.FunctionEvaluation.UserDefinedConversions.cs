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
            nint source = RequiresNullableSourceExtraction(
                argument, conversion, evaluation.Thread)
                ? CreateNullableSourceConversionArgument(
                    evaluation.Pointer,
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

            ManagedExpressionValue converted = CaptureUserDefinedConversionResult(
                value, conversion, out retained, out retainedIsHeapHandle);

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

    private nint CreateNullableSourceConversionArgument(
        nint evaluation,
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
        ManagedBoundType? containedType = null;
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
                if (!actual.IsSameType(conversion.ParameterType) &&
                    !ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                        actual, conversion.ParameterType, conversion.Language))
                {
                    throw new InvalidOperationException(
                        "A lifted conversion's contained value does not match its operator parameter.");
                }

                _ = ComAbi.AddRef(field);
                containedValue = field;
                containedType = actual;
            });
            if (containedValue == 0 || containedType is null)
            {
                throw new InvalidOperationException(
                    "System.Nullable<T> does not expose its required value field.");
            }

            if (!containedType.IsSameType(conversion.ParameterType))
            {
                ManagedValueDisplay display = CorDebugValueFormatter.Format(containedValue);
                ManagedExpressionValue extracted = ManagedExpressionValueFactory.FromVariable(
                    new DebugVariableInfo(
                        "$conversionSource",
                        display.Value,
                        display.Type,
                        VariablesReference: 0,
                        MemoryReference: null,
                        EvaluateName: null),
                    runtimeValueReference: 0,
                    display) with
                {
                    DeclaredType = containedType
                };
                ManagedExpressionValue converted =
                    ManagedPrimitiveConversionEvaluator.ConvertStandardExplicitUserDefinedConversion(
                        extracted,
                        containedType,
                        conversion.ParameterType,
                        conversion.Language) with
                    {
                        DeclaredType = conversion.ParameterType
                    };
                return CreateFunctionArgument(
                    evaluation, converted, runtimeArgument: 0, temporaryArguments);
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

    private ManagedExpressionValue CaptureUserDefinedConversionResult(
        nint value,
        ManagedUserDefinedConversion conversion,
        out nint retained,
        out bool retainedIsHeapHandle)
    {
        retained = 0;
        retainedIsHeapHandle = false;
        if (conversion.ResultType.IsReference)
        {
            if (!TryDereferenceValue(value, out nint dereferenced))
            {
                return ManagedExpressionValueFactory.FromScalar(
                    value: null, conversion.ResultType.DisplayName) with
                {
                    DeclaredType = conversion.ResultType
                };
            }

            _ = ComAbi.Release(dereferenced);
            retained = CreateFunctionEvaluationHandle(value);
            retainedIsHeapHandle = true;
            return CreateMaterializedUserDefinedConversionValue(conversion.ResultType);
        }

        if (conversion.ResultType.ElementType == 0x11)
        {
            _ = ComAbi.AddRef(value);
            retained = value;
            return CreateMaterializedUserDefinedConversionValue(conversion.ResultType);
        }

        ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
        ManagedExpressionValue converted = ManagedExpressionValueFactory.FromVariable(
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
        return converted.HasScalar
            ? converted
            : throw new InvalidOperationException(
                $"The conversion result '{conversion.ResultType.DisplayName}' cannot be materialized.");
    }

    private ManagedExpressionValue ApplyUserDefinedConversionTarget(
        ManagedExpressionValue value,
        ManagedUserDefinedConversion conversion,
        nint thread)
    {
        if (RequiresNullableTargetMaterialization(conversion, thread))
        {
            ManagedBoundType underlying = conversion.TargetType.TypeArguments[0];
            ManagedExpressionValue nullableValue = conversion.ResultType.IsSameType(underlying)
                ? value
                : ManagedPrimitiveConversionEvaluator.ConvertStandardExplicitUserDefinedConversion(
                    value,
                    conversion.ResultType,
                    underlying,
                    conversion.Language) with
                {
                    DeclaredType = underlying
                };
            return nullableValue with
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

    private bool RequiresNullableSourceExtraction(
        ManagedExpressionValue argument,
        ManagedUserDefinedConversion conversion,
        nint thread) => argument.DeclaredType is ManagedBoundType source &&
        RequiresNullableSourceExtraction(source, conversion, thread);

    private bool RequiresNullableSourceExtraction(
        ManagedBoundType source,
        ManagedUserDefinedConversion conversion,
        nint thread) =>
        _boundTypes.IsCoreType(source, "System.Nullable`1", thread) &&
        source.TypeArguments is [ManagedBoundType underlying] &&
        (underlying.IsSameType(conversion.ParameterType) ||
            ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                underlying, conversion.ParameterType, conversion.Language));

    private bool RequiresNullableTargetMaterialization(
        ManagedUserDefinedConversion conversion,
        nint thread) =>
        _boundTypes.IsCoreType(conversion.TargetType, "System.Nullable`1", thread) &&
        conversion.TargetType.TypeArguments is [ManagedBoundType underlying] &&
        (underlying.IsSameType(conversion.ResultType) ||
            ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                conversion.ResultType, underlying, conversion.Language));

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

    private unsafe bool TryContinueWithNullableExplicitResultMaterialization(
        ManagedFunctionEvaluation active)
    {
        ManagedUserDefinedConversion? conversion = active.ExplicitUserDefinedConversion;
        if (active.PendingNullableExplicitResult ||
            conversion is null ||
            !RequiresNullableTargetMaterialization(conversion, active.Thread))
        {
            return false;
        }

        if (active.Arguments.Length != 1 || active.RuntimeArguments.Length != 1)
        {
            throw new InvalidOperationException(
                "A lifted explicit conversion has an invalid argument state.");
        }

        nint completedEvaluation = active.Pointer;
        nint value = 0;
        nint retained = 0;
        bool retainedIsHeapHandle = false;
        nint nextEvaluation = 0;
        nint oldArgument = active.RuntimeArguments[0];
        bool oldArgumentIsHeapHandle = active.RuntimeArgumentIsHeapHandle[0];
        try
        {
            nint* valueAddress = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugEvalAbi(completedEvaluation).GetResult((nint)valueAddress),
                "ICorDebugEval.GetResult");
            value = RequirePointer(
                Volatile.Read(ref *valueAddress), "ICorDebugEval.GetResult");
            ManagedExpressionValue converted = CaptureUserDefinedConversionResult(
                value, conversion, out retained, out retainedIsHeapHandle);
            converted = ApplyUserDefinedConversionTarget(
                converted, conversion, active.Thread);
            if (!converted.RequiresNullableMaterialization)
            {
                throw new InvalidOperationException(
                    "A lifted explicit result did not select nullable materialization.");
            }

            nextEvaluation = CreateEvaluation(active.Thread);
            active.Arguments[0] = converted;
            active.RuntimeArguments[0] = retained;
            active.RuntimeArgumentIsHeapHandle[0] = retainedIsHeapHandle;
            retained = 0;
            ReleaseFunctionEvaluationArgument(oldArgument, oldArgumentIsHeapHandle);
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingNullableExplicitResult = true;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleStructuredValueAllocation(active, conversion.TargetType);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after scheduling a lifted " +
                "conversion result. The target's evaluation state is uncertain; this " +
                "debugger session must be disconnected.");
            return true;
        }
        finally
        {
            ReleaseFunctionEvaluationArgument(retained, retainedIsHeapHandle);
            ReleaseFunctionEvaluationPointer(nextEvaluation);
            ReleaseFunctionEvaluationPointer(completedEvaluation);
            ReleaseFunctionEvaluationPointer(value);
        }
    }

    private void PopulateNullableExplicitResult(
        nint value,
        ManagedFunctionEvaluation active)
    {
        if (!active.PendingNullableExplicitResult)
        {
            return;
        }

        ManagedUserDefinedConversion conversion = active.ExplicitUserDefinedConversion ??
            throw new InvalidOperationException(
                "A pending lifted result has no selected conversion.");
        nint unboxed = 0;
        nint runtimeType = 0;
        try
        {
            if (!TryDereferenceAndUnboxValue(value, out unboxed))
            {
                throw new InvalidOperationException(
                    "CoreCLR did not allocate the lifted nullable result.");
            }

            runtimeType = _boundTypes.ResolveRuntimeType(
                conversion.TargetType, active.Thread);
            SetNullableArgument(
                unboxed,
                runtimeType,
                active.Arguments[0],
                active.RuntimeArguments[0]);
            active.PendingNullableExplicitResult = false;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(runtimeType);
            ReleaseFunctionEvaluationPointer(unboxed);
        }
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
