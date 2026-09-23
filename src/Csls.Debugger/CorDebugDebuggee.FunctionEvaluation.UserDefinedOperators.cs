using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Executes exact user-defined operators through supervised function evaluation.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Determines whether an operator expression root selects target code.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier.</param>
    /// <param name="plan">The validated operator expression.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>True when a loaded user-defined operator is selected.</returns>
    internal bool HasUserDefinedOperator(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(plan, frame.ExpressionLanguage);
        if (plan.Root.Kind is not (DebugExpressionNodeKind.Unary or
            DebugExpressionNodeKind.Binary))
        {
            return false;
        }

        ManagedExpressionValue[] operands = [.. plan.Root.Children.Select(child =>
            EvaluateNode(frame, plan, child, generation))];
        ManagedExpressionValue?[] constantOperands = [.. plan.Root.Children.Select((child, index) =>
            child.Kind == DebugExpressionNodeKind.Literal && operands[index].Scalar is int or long
                ? operands[index]
                : null)];
        nint thread = GetThread(frame.ThreadId);
        try
        {
            ManagedBoundType?[] operandTypes = BindFunctionEvaluationArgumentTypes(
                operands, plan.Language, thread);
            ManagedUserDefinedOperator? selected = new ManagedUserDefinedOperatorResolver(
                _boundTypes, thread, plan.Language).Resolve(
                    plan.Root.Operator, operandTypes, constantOperands);
            return selected is not null &&
                (!selected.IsLifted || !operands.Select((operand, index) =>
                    IsEmptyLiftedOperatorOperand(
                        operand,
                        operandTypes[index],
                        thread)).Any(static empty => empty));
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(thread);
        }
    }

    private ManagedFunctionBinding ResolveUserDefinedOperator(
        DebugExpressionOperator operation,
        ManagedBoundType?[] operands,
        IReadOnlyList<ManagedExpressionValue?> constantOperands,
        DebugExpressionLanguage language,
        nint thread,
        out ManagedUserDefinedOperator selected)
    {
        selected = new ManagedUserDefinedOperatorResolver(
            _boundTypes, thread, language).Resolve(
                operation, operands, constantOperands) ??
            throw new InvalidOperationException(
                $"No loaded user-defined operator exists for '{operation}'.");
        CorDebugLoadedModule module = _boundTypes.GetModule(selected.DeclaringType);
        nint function = 0;
        nint[] typeArguments = [];
        try
        {
            function = GetModuleFunction(module.Pointer, selected.MethodToken);
            typeArguments = ManagedRuntimeTypeArguments.ResolveBound(
                selected.DeclaringType.TypeArguments, _boundTypes, thread);
            var binding = new ManagedFunctionBinding(
                function,
                typeArguments,
                selected.ExpressionResultType,
                [.. selected.ParameterTypes],
                [.. Enumerable.Range(0, operands.Length)],
                new ManagedExpressionValue?[operands.Length]);
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

    private bool TryEvaluateEmptyLiftedOperator(
        ManagedFrameHandle frame,
        DebugExpressionOperator operation,
        IReadOnlyList<ManagedExpressionValue> operands,
        DebugExpressionLanguage language,
        out ManagedExpressionValue result)
    {
        nint thread = GetThread(frame.ThreadId);
        try
        {
            ManagedBoundType?[] operandTypes = BindFunctionEvaluationArgumentTypes(
                [.. operands], language, thread);
            ManagedUserDefinedOperator? selected = new ManagedUserDefinedOperatorResolver(
                _boundTypes, thread, language).Resolve(operation, operandTypes);
            if (selected is not { IsLifted: true })
            {
                result = null!;
                return false;
            }

            bool[] empty = [.. operands.Select((operand, index) =>
                IsEmptyLiftedOperatorOperand(
                    operand,
                    operandTypes[index],
                    thread))];
            if (!empty.Any(static value => value))
            {
                result = null!;
                return false;
            }

            if (_boundTypes.IsCoreType(
                selected.ExpressionResultType, "System.Nullable`1", thread))
            {
                ManagedExpressionValue emptyResult =
                    ManagedExpressionValueFactory.FromZeroValueTypeDefault(
                        selected.ExpressionResultType);
                result = emptyResult with
                {
                    Display = emptyResult.Display with
                    {
                        Value = "null",
                        Type = FormatBoundType(selected.ExpressionResultType, thread)
                    },
                    IsNullableValue = true
                };
                return true;
            }

            bool value = operation switch
            {
                DebugExpressionOperator.Equal => empty.All(static item => item),
                DebugExpressionOperator.NotEqual => !empty.All(static item => item),
                DebugExpressionOperator.LessThan or
                DebugExpressionOperator.LessThanOrEqual or
                DebugExpressionOperator.GreaterThan or
                DebugExpressionOperator.GreaterThanOrEqual => false,
                _ => throw new InvalidOperationException(
                    $"Lifted Boolean operator '{operation}' has invalid empty semantics.")
            };
            result = ManagedExpressionValueFactory.FromScalar(value, "bool");
            return true;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(thread);
        }
    }

    private bool IsEmptyLiftedOperatorOperand(
        ManagedExpressionValue operand,
        ManagedBoundType? operandType,
        nint thread)
    {
        if (operandType is null)
        {
            return operand is
            {
                HasScalar: true,
                Scalar: null,
                RuntimeValueReference: 0
            };
        }

        return _boundTypes.IsCoreType(operandType, "System.Nullable`1", thread) &&
            IsNullableBoxingEmpty(operand, operandType, thread);
    }

    private ManagedExpressionValue PrepareLiftedOperatorArgument(
        ManagedExpressionValue argument,
        ManagedExpressionValue? constant,
        ManagedBoundType source,
        ManagedBoundType parameter,
        DebugExpressionLanguage language,
        nint thread)
    {
        if (_boundTypes.IsCoreType(source, "System.Nullable`1", thread))
        {
            return argument with { DeclaredType = source };
        }

        if (source.IsSameType(parameter))
        {
            return argument with { DeclaredType = parameter };
        }

        if (ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
            source, parameter, language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                argument, source, parameter, language);
        }

        if (constant is not null &&
            ManagedPrimitiveConversionEvaluator.IsImplicitConstantInvocationConversion(
                constant, source, parameter, language))
        {
            return ManagedPrimitiveConversionEvaluator.ConvertInvocationConstant(
                constant, source, parameter, language);
        }

        throw new InvalidOperationException(
            $"The lifted operator cannot receive '{source.DisplayName}' as " +
            $"'{parameter.DisplayName}'.");
    }

    private bool RequiresLiftedOperatorExtraction(
        ManagedExpressionValue argument,
        nint thread) => argument.DeclaredType is ManagedBoundType declared &&
        _boundTypes.IsCoreType(declared, "System.Nullable`1", thread);

    private string FormatBoundType(ManagedBoundType type, nint thread)
    {
        nint runtimeType = 0;
        try
        {
            runtimeType = _boundTypes.ResolveRuntimeType(type, thread);
            return RuntimeTypes.Format(
                runtimeType,
                depth: 0,
                tupleCustomTypeInfo: null,
                out _,
                out _);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(runtimeType);
        }
    }

    private unsafe bool TryContinueWithNullableOperatorResultMaterialization(
        ManagedFunctionEvaluation active)
    {
        ManagedUserDefinedOperator? selected = active.UserDefinedOperator;
        if (active.PendingNullableOperatorResult ||
            selected is not { IsLifted: true } ||
            !_boundTypes.IsCoreType(
                selected.ExpressionResultType, "System.Nullable`1", active.Thread))
        {
            return false;
        }

        if (active.Arguments.Length == 0 || active.RuntimeArguments.Length == 0)
        {
            throw new InvalidOperationException(
                "A lifted operator has no result-materialization slot.");
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
            ManagedExpressionValue operatorResult = CaptureManagedFunctionResult(
                value,
                selected.ResultType,
                "$operator",
                out retained,
                out retainedIsHeapHandle) with
            {
                DeclaredType = selected.ExpressionResultType,
                IsNullableValue = true,
                RequiresNullableMaterialization = true,
                IsMaterializedFunctionArgument = false
            };

            nextEvaluation = CreateEvaluation(active.Thread);
            active.Arguments[0] = operatorResult;
            active.RuntimeArguments[0] = retained;
            active.RuntimeArgumentIsHeapHandle[0] = retainedIsHeapHandle;
            retained = 0;
            ReleaseFunctionEvaluationArgument(oldArgument, oldArgumentIsHeapHandle);
            active.Pointer = nextEvaluation;
            nextEvaluation = 0;
            active.PendingNullableOperatorResult = true;
            _ = ComAbi.Release(completedEvaluation);
            completedEvaluation = 0;

            ScheduleStructuredValueAllocation(active, selected.ExpressionResultType);
            ContinueFunctionEvaluation(
                "The debugger could not resume the target after scheduling a lifted " +
                "operator result. The target's evaluation state is uncertain; this " +
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

    private void PopulateNullableOperatorResult(
        nint value,
        ManagedFunctionEvaluation active)
    {
        if (!active.PendingNullableOperatorResult)
        {
            return;
        }

        ManagedUserDefinedOperator selected = active.UserDefinedOperator ??
            throw new InvalidOperationException(
                "A pending lifted result has no selected operator.");
        nint unboxed = 0;
        nint runtimeType = 0;
        try
        {
            if (!TryDereferenceAndUnboxValue(value, out unboxed))
            {
                throw new InvalidOperationException(
                    "CoreCLR did not allocate the lifted nullable operator result.");
            }

            runtimeType = _boundTypes.ResolveRuntimeType(
                selected.ExpressionResultType, active.Thread);
            SetNullableArgument(
                unboxed,
                runtimeType,
                active.Arguments[0],
                active.RuntimeArguments[0]);
            active.PendingNullableOperatorResult = false;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(runtimeType);
            ReleaseFunctionEvaluationPointer(unboxed);
        }
    }

    private nint CreateLiftedOperatorArgument(
        nint evaluation,
        ManagedExpressionValue argument,
        ManagedUserDefinedOperator selected,
        int index,
        nint runtimeArgument,
        nint thread,
        List<nint> temporaryArguments)
    {
        if (runtimeArgument == 0 ||
            argument.DeclaredType is not ManagedBoundType nullableType ||
            !_boundTypes.IsCoreType(nullableType, "System.Nullable`1", thread) ||
            nullableType.TypeArguments is not [ManagedBoundType underlying] ||
            selected.OperandTypes[index].TypeArguments is not
                [ManagedBoundType effectiveUnderlying] ||
            !underlying.IsSameType(effectiveUnderlying) &&
                !ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
                    underlying, effectiveUnderlying, selected.Language))
        {
            throw new InvalidOperationException(
                "A populated lifted operator has no exact nullable operand storage.");
        }

        nint nullableValue = 0;
        nint runtimeType = 0;
        nint containedValue = 0;
        try
        {
            if (!TryDereferenceAndUnboxValue(runtimeArgument, out nullableValue))
            {
                throw new InvalidOperationException(
                    "A populated lifted operator has no nullable operand storage.");
            }

            runtimeType = _boundTypes.ResolveRuntimeType(nullableType, thread);
            VisitDeclaredRuntimeFields(nullableValue, runtimeType, (name, field) =>
            {
                if (!string.Equals(name, "value", StringComparison.Ordinal))
                {
                    return;
                }

                ManagedBoundType actual = _boundTypes.CaptureValue(field, thread);
                ManagedBoundType parameter = selected.ParameterTypes[index];
                if (!actual.IsSameType(parameter) &&
                    !ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
                        actual, parameter, selected.Language))
                {
                    throw new InvalidOperationException(
                        "A lifted operator's contained value does not match its parameter.");
                }

                _ = ComAbi.AddRef(field);
                containedValue = field;
            });
            if (containedValue == 0)
            {
                throw new InvalidOperationException(
                    "System.Nullable<T> does not expose its required value field.");
            }

            ManagedBoundType parameterType = selected.ParameterTypes[index];
            if (underlying.IsSameType(parameterType))
            {
                temporaryArguments.Add(containedValue);
                nint result = containedValue;
                containedValue = 0;
                return result;
            }

            ManagedValueDisplay display = CorDebugValueFormatter.Format(containedValue);
            ManagedExpressionValue extracted = ManagedExpressionValueFactory.FromVariable(
                new DebugVariableInfo(
                    "$operatorOperand",
                    display.Value,
                    display.Type,
                    VariablesReference: 0,
                    MemoryReference: null,
                    EvaluateName: null),
                runtimeValueReference: 0,
                display) with
            {
                DeclaredType = underlying
            };
            ManagedExpressionValue converted =
                ManagedPrimitiveConversionEvaluator.ConvertForInvocation(
                    extracted,
                    underlying,
                    parameterType,
                    selected.Language) with
                {
                    DeclaredType = parameterType
                };
            return CreateFunctionArgument(
                evaluation, converted, runtimeArgument: 0, temporaryArguments);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(containedValue);
            ReleaseFunctionEvaluationPointer(runtimeType);
            ReleaseFunctionEvaluationPointer(nullableValue);
        }
    }
}
