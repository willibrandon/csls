using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains expression runtime identities independently of debugger presentation handles.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedExpressionValue EvaluateRuntimeRoot(
        ManagedFrameHandle frame,
        string name,
        DebugStopGeneration generation)
    {
        (nint value, ManagedTupleCustomTypeInfo? tupleCustomTypeInfo, ManagedValueOrigin? origin, ManagedBoundType? declaredType) = ResolveFrameValue(
            frame, name, allowInstanceReceiver: true);
        try
        {
            return RetainExpressionValue(
                name, name, value, frame.Id, generation, tupleCustomTypeInfo, origin, declaredType);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
        }
    }

    private ManagedExpressionValue RetainExpressionValue(
        string name,
        string? evaluateName,
        nint value,
        int frameId,
        DebugStopGeneration generation,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedValueOrigin? origin,
        ManagedBoundType? declaredType = null)
    {
        (ManagedValueDisplay runtimeValue, ManagedValueDisplay formatted) = FormatRuntimeValuePair(
            value, debuggerDisplayDepth: 0, tupleCustomTypeInfo);
        ManagedValueReferences references = RetainValue(
            value, generation, evaluateName, frameId,
            tupleCustomTypeInfo: tupleCustomTypeInfo, origin: origin);
        ManagedExpressionValue expression = ManagedExpressionValueFactory.FromVariable(
            new DebugVariableInfo(
                name, formatted.Value, formatted.Type,
                references.VariablesReference, references.MemoryReference, evaluateName),
            runtimeValueReference: 0,
            runtimeValue) with
        { DeclaredType = declaredType };
        if (expression.HasScalar && expression.Scalar is not string &&
            ManagedRuntimeValueIdentity.GetElementType(value) is not (0x11 or 0x0e or 0x12 or 0x14 or 0x1c or 0x1d))
        {
            return expression;
        }

        ManagedValueHandle retained = RetainRuntimeValue(
            value,
            generation,
            evaluateName,
            frameId,
            GetValueThreadId(frameId),
            ManagedValueView.Default,
            tupleCustomTypeInfo,
            origin);
        return expression with { RuntimeValueReference = retained.Id };
    }

    private ManagedTupleCustomTypeInfo? GetExpressionTupleCustomTypeInfo(
        ManagedExpressionValue value)
    {
        if (value.RuntimeValueReference <= 0)
        {
            return null;
        }

        if (!_values.TryGetValue(value.RuntimeValueReference, out ManagedValueHandle? retained))
        {
            throw new InvalidOperationException(
                $"Variable reference {value.RuntimeValueReference} is stale or unknown.");
        }

        ValidateValueLifetime(retained);
        return retained.TupleCustomTypeInfo;
    }

    private ManagedExpressionValue RetainValueTypeConversion(
        ManagedExpressionValue value,
        ManagedFrameHandle frame,
        DebugStopGeneration generation,
        bool retainPrimitive = false)
    {
        if (!TryDereferenceAndUnboxValue(GetRuntimeValue(value), out nint unboxed))
        {
            throw new InvalidOperationException("A null reference cannot be unboxed to a value type.");
        }

        try
        {
            // CoreCLR retains a local value-class snapshot. The cast produces a value,
            // so its fields have no writable origin in the original box or local.
            ManagedExpressionValue retainedValue = RetainExpressionValue(
                value.Display.Name, value.Display.EvaluateName, unboxed, frame.Id, generation,
                tupleCustomTypeInfo: null, origin: null, value.DeclaredType) with
            {
                ExplicitReceiverType = value.ExplicitReceiverType
            };
            if (!retainPrimitive || retainedValue.RuntimeValueReference > 0)
            {
                return retainedValue;
            }

            ManagedValueHandle retained = RetainRuntimeValue(
                unboxed,
                generation,
                retainedValue.Display.EvaluateName,
                frame.Id,
                frame.ThreadId,
                ManagedValueView.Default,
                tupleCustomTypeInfo: null);
            return retainedValue with { RuntimeValueReference = retained.Id };
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(unboxed);
        }
    }

    private ManagedExpressionValue EvaluateNullableTryCast(
        ManagedExpressionValue operand,
        ManagedBoundType? declaredSource,
        ManagedBoundType? actualSource,
        ManagedBoundType target,
        string targetDisplayName,
        ManagedFrameHandle frame,
        DebugStopGeneration generation,
        DebugExpressionLanguage language,
        nint thread)
    {
        if (target.TypeArguments is not [ManagedBoundType underlying])
        {
            throw new BadImageFormatException("System.Nullable<T> does not expose one loaded type argument.");
        }

        if (declaredSource is not null &&
            !declaredSource.IsSameType(target) &&
            (!declaredSource.IsReference ||
             !_referenceConversions.IsRuntimeAssignable(underlying, declaredSource, thread)))
        {
            throw new InvalidOperationException(
                $"No built-in nullable unboxing conversion exists from " +
                $"'{declaredSource.DisplayName}' to '{target.DisplayName}'.");
        }

        string? evaluateName = ManagedExpressionName.CreateTypeOperation(
            operand.Display.EvaluateName, targetDisplayName, DebugExpressionNodeKind.TryCast, language);
        if (actualSource is null ||
            !actualSource.IsSameType(underlying) && !actualSource.IsSameType(target))
        {
            ManagedExpressionValue empty = ManagedExpressionValueFactory.FromZeroValueTypeDefault(target);
            return empty with
            {
                Display = empty.Display with
                {
                    Value = "null",
                    Type = targetDisplayName,
                    EvaluateName = evaluateName
                },
                ExplicitReceiverType = target,
                IsNullableValue = true,
                RequiresNullableMaterialization = true
            };
        }

        ManagedExpressionValue projected = actualSource.IsSameType(target)
            ? operand with { DeclaredType = target, ExplicitReceiverType = target }
            : RetainValueTypeConversion(
                operand with { DeclaredType = target, ExplicitReceiverType = target }, frame, generation,
                retainPrimitive: true);
        bool emptyNullable = actualSource.IsSameType(target) &&
            string.Equals(projected.Display.Value, "null", StringComparison.Ordinal);
        return projected with
        {
            Display = projected.Display with
            {
                Type = targetDisplayName,
                EvaluateName = evaluateName
            },
            IsZeroValueTypeDefault = emptyNullable,
            IsNullableValue = true,
            RequiresNullableMaterialization = !actualSource.IsSameType(target)
        };
    }

    private static bool TryEvaluateNullableMember(
        ManagedExpressionValue receiver,
        string name,
        DebugExpressionLanguage language,
        out ManagedExpressionValue? result)
    {
        result = null;
        if (!receiver.IsNullableValue || receiver.DeclaredType is not { TypeArguments: [ManagedBoundType underlying] })
        {
            return false;
        }

        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? evaluateName = ManagedExpressionName.CreateMember(receiver.Display.EvaluateName, name);
        if (string.Equals(name, "HasValue", comparison))
        {
            ManagedExpressionValue hasValue = ManagedExpressionValueFactory.FromScalar(
                !receiver.IsZeroValueTypeDefault, "bool");
            result = hasValue with
            {
                Display = hasValue.Display with
                {
                    Name = name,
                    EvaluateName = evaluateName
                }
            };
            return true;
        }

        if (!string.Equals(name, "Value", comparison))
        {
            return false;
        }

        if (receiver.IsZeroValueTypeDefault)
        {
            throw new InvalidOperationException("Nullable object must have a value.");
        }

        result = receiver with
        {
            Display = receiver.Display with
            {
                Name = name,
                Type = receiver.Type,
                EvaluateName = evaluateName
            },
            DeclaredType = underlying,
            ExplicitReceiverType = underlying,
            IsNullableValue = false,
            RequiresNullableMaterialization = false
        };
        return true;
    }

    private int[] EvaluateArrayIndexes(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        int[] indexes = new int[node.Children.Count - 1];
        for (int index = 0; index < indexes.Length; index++)
        {
            indexes[index] = ManagedExpressionValueFactory.RequireArrayIndex(EvaluateNode(
                frame, plan, node.Children[index + 1], generation));
        }

        return indexes;
    }
}
