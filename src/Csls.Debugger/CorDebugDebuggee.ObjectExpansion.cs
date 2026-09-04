using Csls.Debugger.Contracts;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Connects generation-owned debuggee state to managed object expansion.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private List<DebugVariableInfo> ExpandObject(
        nint value,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count,
        ManagedValueView view) => _objectExpander.Expand(
            value,
            parentEvaluateName,
            frameId,
            generation,
            start,
            count,
            view);

    PEReader IManagedObjectExpansionServices.OpenRuntimeModule(nint module) =>
        OpenRuntimeModule(module);

    ManagedValueDisplay IManagedObjectExpansionServices.FormatRuntimeValue(nint value) =>
        FormatRuntimeValue(value);

    ManagedValueDisplay IManagedDebuggerDisplayServices.FormatRuntimeValue(
        nint value,
        int debuggerDisplayDepth) => FormatRuntimeValue(value, debuggerDisplayDepth);

    PEReader IManagedDebuggerDisplayServices.OpenRuntimeModule(nint module) =>
        OpenRuntimeModule(module);

    ManagedValueReferences IManagedObjectExpansionServices.RetainValue(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        ManagedValueView view) => RetainValue(
            value,
            generation,
            evaluateName,
            frameId,
            view);

    bool IManagedObjectExpansionServices.TryDereferenceValue(
        nint value,
        out nint result) => TryDereferenceValue(value, out result);

    List<DebugVariableInfo> IManagedObjectExpansionServices.ExpandArray(
        nint array,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        int start,
        int count) => ExpandArray(
            array,
            parentEvaluateName,
            frameId,
            generation,
            start,
            count);
}
