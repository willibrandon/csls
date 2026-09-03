using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Evaluates side-effect-free expressions against generation-bound managed frames.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Resolves a safe expression without executing code in the target process.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="expression">The local-rooted expression to resolve.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The formatted current-generation expression value.</returns>
    internal DebugEvaluateResult Evaluate(
        int frameId,
        string expression,
        DebugStopGeneration generation)
    {
        ManagedExpressionPlan plan = ManagedSideEffectFreeExpressionParser.Parse(expression);
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        DebugVariableInfo current = ResolveRoot(frame, plan.RootName, generation);
        foreach (ManagedExpressionSegment segment in plan.Segments)
        {
            if (current.VariablesReference == 0)
            {
                throw new InvalidOperationException(
                    $"'{current.EvaluateName ?? current.Name}' has no expandable children.");
            }

            IReadOnlyList<DebugVariableInfo> children = GetVariables(
                current.VariablesReference,
                generation,
                start: 0,
                count: 0);
            current = children.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                segment.Name,
                StringComparison.Ordinal)) ?? throw new InvalidOperationException(
                    $"The expression {Describe(segment.Kind)} '{segment.Name}' is unavailable.");
        }

        return new DebugEvaluateResult(
            current.Value,
            current.Type,
            current.VariablesReference,
            current.MemoryReference);
    }

    private static string Describe(ManagedExpressionSegmentKind kind) => kind switch
    {
        ManagedExpressionSegmentKind.Member => "member",
        ManagedExpressionSegmentKind.ArrayIndex => "array index",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private DebugVariableInfo ResolveRoot(
        ManagedFrameHandle frame,
        string rootName,
        DebugStopGeneration generation)
    {
        string runtimeRootName = string.Equals(rootName, "Me", StringComparison.Ordinal)
            ? "this"
            : rootName;
        DebugVariableInfo? local = EnumerateValues(
            frame.Pointer,
            ManagedScopeKind.Locals,
            GetVariableNames(frame, ManagedScopeKind.Locals),
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                runtimeRootName,
                StringComparison.Ordinal));
        if (local is not null)
        {
            return local;
        }

        DebugVariableInfo? argument = EnumerateValues(
            frame.Pointer,
            ManagedScopeKind.Arguments,
            GetVariableNames(frame, ManagedScopeKind.Arguments),
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                runtimeRootName,
                StringComparison.Ordinal));
        return argument ?? throw new InvalidOperationException(
            $"The expression root '{rootName}' is unavailable in the selected frame.");
    }
}
