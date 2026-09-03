using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates immediate values from managed frame scopes.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe List<DebugVariableInfo> EnumerateValues(
        nint frame,
        ManagedScopeKind kind,
        IReadOnlyDictionary<int, string> names,
        DebugStopGeneration generation,
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
                        bool hasSourceName = names.TryGetValue(index, out string? sourceName) &&
                            !string.IsNullOrEmpty(sourceName);
                        string name = hasSourceName
                                ? sourceName!
                                : kind == ManagedScopeKind.Arguments
                                    ? $"argument {index}"
                                    : $"local {index}";
                        string? evaluateName = hasSourceName ? sourceName : null;
                        ManagedValueReferences references = RetainValue(
                            value,
                            generation,
                            evaluateName);
                        result.Add(new DebugVariableInfo(
                            name,
                            display.Value,
                            display.Type,
                            references.VariablesReference,
                            references.MemoryReference,
                            evaluateName));
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
