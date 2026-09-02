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
    /// <returns>The requested immediate variable page.</returns>
    internal IReadOnlyList<DebugVariableInfo> GetVariables(
        int variablesReference,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ManagedScopeHandle scope = _scopes.Values.FirstOrDefault(
            candidate => candidate.Id == variablesReference)
            ?? throw new InvalidOperationException(
                $"Variable reference {variablesReference} is stale or unknown.");
        if (scope.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Variable reference {variablesReference} belongs to a retired stop generation.");
        }

        ManagedFrameHandle frame = GetFrame(scope.FrameId, generation);
        IReadOnlyDictionary<int, string> names = GetVariableNames(frame, scope.Kind);
        return EnumerateValues(frame.Pointer, scope.Kind, names, start, count);
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

    private static IReadOnlyDictionary<int, string> GetVariableNames(
        ManagedFrameHandle frame,
        ManagedScopeKind kind)
    {
        try
        {
            return kind == ManagedScopeKind.Arguments
                ? PortablePdbVariableNameResolver.GetArguments(frame.ModulePath, frame.MethodToken)
                : PortablePdbVariableNameResolver.GetLocals(
                    frame.ModulePath,
                    frame.MethodToken,
                    frame.IlOffset);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new Dictionary<int, string>();
        }
    }

    private static unsafe List<DebugVariableInfo> EnumerateValues(
        nint frame,
        ManagedScopeKind kind,
        IReadOnlyDictionary<int, string> names,
        int start,
        int count)
    {
        const int maximumValueCount = 64 * 1024;
        nint ilFrame = 0;
        nint enumerator = 0;
        try
        {
            ilFrame = ComAbi.QueryInterface(frame, ICorDebugILFrameAbi.InterfaceId);
            nint* enumeratorAddress = &enumerator;
            var api = new ICorDebugILFrameAbi(ilFrame);
            int enumerateResult = kind == ManagedScopeKind.Arguments
                ? api.EnumerateArguments((nint)enumeratorAddress)
                : api.EnumerateLocalVariables((nint)enumeratorAddress);
            CorDebugHResult.ThrowIfFailed(enumerateResult, $"ICorDebugILFrame.Enumerate{kind}");
            enumerator = Volatile.Read(ref *enumeratorAddress);
            if (enumerator == 0)
            {
                throw new InvalidOperationException(
                    $"ICorDebugILFrame.Enumerate{kind} returned no enumerator.");
            }

            List<DebugVariableInfo> result = [];
            var values = new ICorDebugValueEnumAbi(enumerator);
            for (int index = 0; index < maximumValueCount; index++)
            {
                nint value = 0;
                uint fetched = 0;
                nint* valueAddress = &value;
                uint* fetchedAddress = &fetched;
                int nextResult = values.Next(1, (nint)valueAddress, (nint)fetchedAddress);
                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugValueEnum.Next");
                value = Volatile.Read(ref *valueAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    return result;
                }

                try
                {
                    if (index >= start && (count == 0 || result.Count < count))
                    {
                        ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
                        string name = names.TryGetValue(index, out string? sourceName) &&
                            !string.IsNullOrEmpty(sourceName)
                                ? sourceName
                                : kind == ManagedScopeKind.Arguments
                                    ? $"argument {index}"
                                    : $"local {index}";
                        result.Add(new DebugVariableInfo(
                            name,
                            display.Value,
                            display.Type,
                            VariablesReference: 0));
                    }
                }
                finally
                {
                    if (value != 0)
                    {
                        _ = ComAbi.Release(value);
                    }
                }

                if (count > 0 && result.Count == count)
                {
                    return result;
                }
            }

            throw new InvalidOperationException(
                $"The scope exceeds the debugger value limit of {maximumValueCount}.");
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }

            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }
    }
}
