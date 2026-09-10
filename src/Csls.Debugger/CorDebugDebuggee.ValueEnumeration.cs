using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates immediate values from managed frame scopes.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private List<DebugVariableInfo> EnumerateValues(
        ManagedFrameHandle frame,
        ManagedScopeKind kind,
        IReadOnlyDictionary<int, ManagedSymbolVariable> names,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        if (kind == ManagedScopeKind.Arguments &&
            ManagedStateMachineArgumentResolver.Resolve(frame) is { } capturedArguments)
        {
            return EnumerateStateMachineVariables(frame, capturedArguments, generation, start, count);
        }

        IReadOnlyList<ManagedStateMachineVariable> hoistedLocals = kind == ManagedScopeKind.Locals
            ? ManagedStateMachineLocalResolver.Resolve(frame) : [];
        List<DebugVariableInfo> result = EnumerateStateMachineVariables(frame, hoistedLocals, generation, start, count);
        if (count == 0 || result.Count < count)
        {
            result.AddRange(EnumeratePhysicalValues(frame, kind, names, generation,
                Math.Max(0, start - hoistedLocals.Count), count == 0 ? 0 : count - result.Count));
        }

        return result;
    }

    private unsafe List<DebugVariableInfo> EnumeratePhysicalValues(
        ManagedFrameHandle frame, ManagedScopeKind kind, IReadOnlyDictionary<int, ManagedSymbolVariable> names,
        DebugStopGeneration generation, int start, int count)
    {
        const int maximumValueCount = 64 * 1024;
        nint ilFrame = 0;
        nint enumerator = 0;
        try
        {
            ilFrame = ComAbi.QueryInterface(frame.Pointer, ICorDebugILFrameAbi.InterfaceId);
            nint* enumeratorAddress = &enumerator;
            var api = new ICorDebugILFrameAbi(ilFrame);
            int enumerateResult = kind == ManagedScopeKind.Arguments
                ? api.EnumerateArguments((nint)enumeratorAddress)
                : api.EnumerateLocalVariables((nint)enumeratorAddress);
            enumerator = Volatile.Read(ref *enumeratorAddress);
            CorDebugHResult.ThrowIfFailed(enumerateResult, $"ICorDebugILFrame.Enumerate{kind}");
            if (enumerator == 0)
            {
                throw new InvalidOperationException(
                    $"ICorDebugILFrame.Enumerate{kind} returned no enumerator.");
            }

            uint total = 0;
            CorDebugHResult.ThrowIfFailed(new ICorDebugEnumAbi(enumerator).GetCount((nint)(&total)),
                "ICorDebugEnum.GetCount");
            total = Volatile.Read(ref total);
            if (total > maximumValueCount)
            {
                throw new InvalidOperationException(
                    $"The scope exceeds the debugger value limit of {maximumValueCount}.");
            }

            int length = start >= total ? 0 : (int)total - start;
            length = count == 0 ? length : Math.Min(count, length);
            List<DebugVariableInfo> result = [];
            for (int index = start; index - start < length; index++)
            {
                _valueRead?.CheckCancellation();
                nint value = 0;
                try
                {
                    _ = names.TryGetValue(index, out ManagedSymbolVariable? sourceVariable);
                    string? sourceName = sourceVariable?.Name;
                    ManagedTupleCustomTypeInfo? tupleCustomTypeInfo = sourceVariable?.TupleCustomTypeInfo;
                    string name = !string.IsNullOrEmpty(sourceName)
                        ? sourceName
                        : kind == ManagedScopeKind.Arguments ? $"argument {index}" : $"local {index}";
                    int getResult = kind == ManagedScopeKind.Arguments
                        ? api.GetArgument((uint)index, (nint)(&value))
                        : api.GetLocalVariable((uint)index, (nint)(&value));
                    value = Volatile.Read(ref value);
                    // CORDBG_E_IL_VAR_NOT_AVAILABLE describes this slot's native lifetime only.
                    if (getResult == unchecked((int)0x80131304))
                    {
                        string type = frame.ModuleId is null || frame.MethodToken == 0 ? string.Empty
                            : FormatDeclaredVariableType(frame,
                                ResolveFrameDeclaredType(frame, kind, index), tupleCustomTypeInfo);
                        result.Add(new DebugVariableInfo(name, "<unavailable at the current instruction>", type,
                            0, null, null, DebugVariablePresentationKind.Unavailable));
                        continue;
                    }

                    CorDebugHResult.ThrowIfFailed(getResult, $"ICorDebugILFrame.Get{kind}");
                    value = RequirePointer(value, $"ICorDebugILFrame.Get{kind}");
                    ManagedValueDisplay display = FormatRuntimeValue(value, tupleCustomTypeInfo);
                    string? evaluateName = string.IsNullOrEmpty(sourceName) ? null : sourceName;
                    ManagedValueReferences references = RetainValue(value, generation, evaluateName, frame.Id,
                        tupleCustomTypeInfo: tupleCustomTypeInfo, origin: frame.CreateValueOrigin(kind, index));
                    result.Add(new DebugVariableInfo(name, display.Value, display.Type,
                        references.VariablesReference, references.MemoryReference, evaluateName));
                }
                finally
                {
                    if (value != 0)
                    {
                        _ = ComAbi.Release(value);
                    }
                }
            }

            return result;
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
