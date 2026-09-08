using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Connects source receiver precedence and static storage to stopped expression operations.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private bool TryResolveStaticReceiver(ManagedFrameHandle frame, DebugExpressionNode node, out string typeName)
    {
        if (!TryGetQualifiedTypeName(node, out typeName))
        {
            return false;
        }
        DebugExpressionNode root = node;
        while (root.Kind == DebugExpressionNodeKind.MemberAccess)
        {
            root = root.Children[0];
        }
        StringComparison comparison = frame.ExpressionLanguage == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string name = root.Text!;
        if (FindVariableIndex(GetVariableNames(frame, ManagedScopeKind.Locals), name, comparison) is not null ||
            ManagedStateMachineLocalResolver.Resolve(frame).Any(candidate => string.Equals(candidate.Name, name, comparison)) ||
            ManagedStateMachineArgumentResolver.Resolve(frame) is { } captured &&
                captured.Any(candidate => string.Equals(candidate.Name, name, comparison)) ||
            FindVariableIndex(GetVariableNames(frame, ManagedScopeKind.Arguments), name, comparison) is not null)
        {
            return false;
        }
        return ManagedRuntimeTypeAliases.TryNormalize(typeName, frame.ExpressionLanguage, out _, out _) ||
            _typeNames.Find(typeName, frame.ExpressionLanguage, "member receiver") is not null;
    }

    private ManagedExpressionValue EvaluateStaticField(
        ManagedFrameHandle frame, string typeName, string name, DebugStopGeneration generation)
    {
        nint thread = GetThread(frame.ThreadId);
        nint value = 0;
        try
        {
            var resolver = new ManagedStaticFieldResolver(_sourceBreakpoints, _boundTypes);
            ManagedStaticFieldBinding field = resolver.Resolve(typeName, name, frame.ExpressionLanguage, thread);
            string evaluateName = $"{typeName}.{name}";
            if (field.IsLiteral)
            {
                string displayType = ManagedRuntimeTypeAliases.TryNormalize(field.FieldType.Name,
                    frame.ExpressionLanguage, out _, out string alias) ? alias : field.FieldType.DisplayName;
                ManagedExpressionValue literal = ManagedExpressionValueFactory.FromScalar(field.Constant, displayType);
                return literal with { Display = literal.Display with { Name = name, EvaluateName = evaluateName }, DeclaredType = field.FieldType };
            }
            value = resolver.Read(field.DeclaringType, field.FieldToken, frame, thread);
            ManagedStaticFieldValueOrigin? origin = field.IsWritable
                ? new ManagedStaticFieldValueOrigin(field.DeclaringType, field.FieldToken, frame.ThreadId) : null;
            return RetainExpressionValue(name, evaluateName, value, frame.Id, generation,
                field.TupleCustomTypeInfo, origin, field.FieldType);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
            _ = ComAbi.Release(thread);
        }
    }

    private ManagedAssignmentTarget ResolveStaticFieldAssignmentTarget(ManagedFrameHandle frame, string typeName, string name)
    {
        nint thread = GetThread(frame.ThreadId);
        try
        {
            var resolver = new ManagedStaticFieldResolver(_sourceBreakpoints, _boundTypes);
            ManagedStaticFieldBinding field = resolver.Resolve(typeName, name, frame.ExpressionLanguage, thread);
            if (!field.IsWritable)
            {
                throw new InvalidOperationException($"Static field '{typeName}.{name}' is readonly or constant.");
            }
            var origin = new ManagedStaticFieldValueOrigin(field.DeclaringType, field.FieldToken, frame.ThreadId);
            string evaluateName = $"{typeName}.{name}";
            return ManagedAssignmentTarget.TakeOwnership(
                (resolver.Read(field.DeclaringType, field.FieldToken, frame, thread), field.TupleCustomTypeInfo, origin, field.FieldType),
                evaluateName);
        }
        finally
        {
            _ = ComAbi.Release(thread);
        }
    }

    private nint ReacquireStaticField(ManagedFrameHandle frame, ManagedStaticFieldValueOrigin origin)
    {
        if (origin.ThreadId != frame.ThreadId)
        {
            throw new InvalidOperationException("The static field belongs to a different selected thread.");
        }
        nint thread = GetThread(frame.ThreadId);
        try
        {
            return new ManagedStaticFieldResolver(_sourceBreakpoints, _boundTypes)
                .Read(origin.DeclaringType, origin.FieldToken, frame, thread);
        }
        finally
        {
            _ = ComAbi.Release(thread);
        }
    }
}
