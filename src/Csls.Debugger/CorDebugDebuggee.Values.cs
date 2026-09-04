using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates generation-bound managed scopes and values.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Creates argument and local scopes for a generation-bound managed frame.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The runtime-backed scopes available for the frame.</returns>
    internal IReadOnlyList<DebugScopeInfo> GetScopes(
        int frameId,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        if (!ComAbi.TryQueryInterface(
            frame.Pointer,
            ICorDebugILFrameAbi.InterfaceId,
            out nint ilFrame))
        {
            return [];
        }

        _ = ComAbi.Release(ilFrame);
        return
        [
            CreateScope(frame, ManagedScopeKind.Arguments, "Arguments"),
            CreateScope(frame, ManagedScopeKind.Locals, "Locals")
        ];
    }

    /// <summary>
    /// Enumerates and formats one page of values from a generation-bound frame scope.
    /// </summary>
    /// <param name="variablesReference">The session-local variable-container handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <param name="start">The zero-based first value to return.</param>
    /// <param name="count">The maximum count, or zero for all remaining values.</param>
    /// <param name="filter">The child category to select before applying pagination.</param>
    /// <returns>The requested immediate variable page.</returns>
    internal IReadOnlyList<DebugVariableInfo> GetVariables(
        int variablesReference,
        DebugStopGeneration generation,
        int start,
        int count,
        DebugVariableFilter filter = DebugVariableFilter.All)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        ManagedScopeHandle? scope = _scopes.Values.FirstOrDefault(
            candidate => candidate.Id == variablesReference);
        if (scope is null)
        {
            return ExpandValue(variablesReference, generation, start, count, filter);
        }

        ValidateGeneration(variablesReference, scope.Generation, generation);
        ManagedFrameHandle frame = GetFrame(scope.FrameId, generation);
        if (filter == DebugVariableFilter.Indexed)
        {
            return [];
        }

        IReadOnlyDictionary<int, ManagedSymbolVariable> names = GetVariableNames(
            frame,
            scope.Kind);
        return EnumerateValues(frame, scope.Kind, names, generation, start, count);
    }

    private DebugScopeInfo CreateScope(
        ManagedFrameHandle frame,
        ManagedScopeKind kind,
        string name)
    {
        (int FrameId, ManagedScopeKind Kind) key = (frame.Id, kind);
        if (!_scopes.TryGetValue(key, out ManagedScopeHandle? scope))
        {
            scope = new ManagedScopeHandle
            {
                Id = checked(++_nextVariablesReference),
                FrameId = frame.Id,
                Generation = frame.Generation,
                Kind = kind
            };
            _scopes.Add(key, scope);
        }

        return new DebugScopeInfo(name, scope.Id, Expensive: false);
    }

    private static IReadOnlyDictionary<int, ManagedSymbolVariable> GetVariableNames(
        ManagedFrameHandle frame,
        ManagedScopeKind kind)
    {
        try
        {
            return kind == ManagedScopeKind.Arguments
                ? ManagedSymbolVariableNameResolver.GetArguments(frame)
                : ManagedSymbolVariableNameResolver.GetLocals(frame);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new Dictionary<int, ManagedSymbolVariable>();
        }
    }

    private static void ValidateGeneration(
        int variablesReference,
        DebugStopGeneration actual,
        DebugStopGeneration expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Variable reference {variablesReference} belongs to a retired stop generation.");
        }
    }
}
