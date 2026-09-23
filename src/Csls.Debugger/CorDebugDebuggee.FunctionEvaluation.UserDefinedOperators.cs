using Csls.Debugger.Contracts;

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
            return new ManagedUserDefinedOperatorResolver(
                _boundTypes, thread, plan.Language).Resolve(
                    plan.Root.Operator, operandTypes, constantOperands) is not null;
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
        nint thread)
    {
        ManagedUserDefinedOperator selected = new ManagedUserDefinedOperatorResolver(
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
                selected.ResultType,
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
}
