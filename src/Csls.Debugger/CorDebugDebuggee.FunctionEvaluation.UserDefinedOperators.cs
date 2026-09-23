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
                    IsNullableBoxingEmpty(
                        operand,
                        operandTypes[index] ?? throw new InvalidOperationException(
                            "A lifted operator has no exact nullable operand type."),
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
                IsNullableBoxingEmpty(
                    operand,
                    operandTypes[index] ?? throw new InvalidOperationException(
                        "A lifted operator has no exact nullable operand type."),
                    thread))];
            if (!empty.Any(static value => value))
            {
                result = null!;
                return false;
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

    private nint CreateLiftedOperatorArgument(
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
            !underlying.IsSameType(selected.ParameterTypes[index]))
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
                if (!actual.IsSameType(underlying))
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
}
