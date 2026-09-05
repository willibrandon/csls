using Csls.Debugger.Contracts;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Supplies generation-owned runtime operations to managed object expansion.
/// </summary>
internal interface IManagedObjectExpansionServices
{
    /// <summary>
    /// Opens the current image for one loaded runtime module.
    /// </summary>
    /// <param name="module">The retained ICorDebugModule pointer.</param>
    /// <returns>A reader over the current managed module image.</returns>
    PEReader OpenRuntimeModule(nint module);

    /// <summary>
    /// Formats one runtime value without implicit target-code execution.
    /// </summary>
    /// <param name="value">The retained ICorDebugValue pointer.</param>
    /// <param name="debuggerDisplayDepth">The current debugger-display recursion depth.</param>
    /// <param name="tupleCustomTypeInfo">The optional tuple-name transforms.</param>
    /// <returns>The formatted value and exact runtime type.</returns>
    ManagedValueDisplay FormatRuntimeValue(
        nint value,
        int debuggerDisplayDepth,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo);

    /// <summary>
    /// Applies one member display to a value using its containing runtime object.
    /// </summary>
    /// <param name="container">The retained containing object value.</param>
    /// <param name="ordinaryDisplay">The field value's ordinary presentation.</param>
    /// <param name="debuggerDisplayDepth">The current debugger-display recursion depth.</param>
    /// <param name="attribute">The member display metadata to apply.</param>
    /// <returns>The member presentation, or the ordinary presentation on safe fallback.</returns>
    ManagedValueDisplay ApplyMemberDisplay(
        nint container,
        ManagedValueDisplay ordinaryDisplay,
        int debuggerDisplayDepth,
        ManagedDebuggerDisplayAttribute attribute);

    /// <summary>
    /// Retains one expandable runtime value for the current stop generation.
    /// </summary>
    /// <param name="value">The retained ICorDebugValue pointer.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="evaluateName">The optional source expression for the value.</param>
    /// <param name="frameId">The optional generation-owned frame identifier.</param>
    /// <param name="view">The presentation view used for later expansion.</param>
    /// <param name="tupleCustomTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="origin">The physical source storage before any presentation transformations.</param>
    /// <param name="lifetime">The containing snapshot's optional retirement state.</param>
    /// <returns>The retained expansion and memory references.</returns>
    ManagedValueReferences RetainValue(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        ManagedValueView view,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedValueOrigin? origin = null,
        ManagedResultsViewLifetime? lifetime = null);

    /// <summary>
    /// Retains a lazy enumerable view without executing target code.
    /// </summary>
    /// <param name="value">The retained, dereferenced runtime value.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="evaluateName">The original expression when available.</param>
    /// <param name="frameId">The owning frame when available.</param>
    /// <param name="threadId">The inherited evaluation thread when available.</param>
    /// <param name="view">The enclosing presentation mode.</param>
    /// <param name="origin">The original receiver's physical storage before unboxing.</param>
    /// <param name="lifetime">The containing snapshot's optional retirement state.</param>
    /// <returns>The retained lazy view or completed snapshot, or null when ineligible.</returns>
    DebugVariableInfo? TryRetainResultsView(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        int? threadId,
        ManagedValueView view,
        ManagedValueOrigin? origin,
        ManagedResultsViewLifetime? lifetime);

    /// <summary>
    /// Identifies a field's physical storage within an exact runtime owner.
    /// </summary>
    /// <param name="parent">The original containing value's storage.</param>
    /// <param name="declaringClass">The loaded class declaring the field.</param>
    /// <param name="fieldToken">The exact metadata field token.</param>
    /// <returns>The field origin, or null when the owner is not identifiable.</returns>
    ManagedValueOrigin? CreateFieldValueOrigin(
        ManagedValueOrigin? parent, nint declaringClass, uint fieldToken);

    /// <summary>
    /// Selects the physical heap or storage origin for a retained container's children.
    /// </summary>
    /// <param name="variablesReference">The retained container reference.</param>
    /// <returns>The physical origin, or null for an unlocated value.</returns>
    ManagedValueOrigin? GetValueOrigin(int variablesReference);

    /// <summary>
    /// Dereferences one runtime value while retaining the resulting interface.
    /// </summary>
    /// <param name="value">The retained ICorDebugValue pointer.</param>
    /// <param name="result">Receives the retained dereferenced value, or zero for null.</param>
    /// <returns>True when a non-null value was returned.</returns>
    bool TryDereferenceValue(nint value, out nint result);

    /// <summary>
    /// Expands one page from a retained managed array value.
    /// </summary>
    /// <param name="array">The retained ICorDebugArrayValue pointer.</param>
    /// <param name="parentEvaluateName">The optional source expression for the array.</param>
    /// <param name="frameId">The optional generation-owned frame identifier.</param>
    /// <param name="generation">The owning stop generation.</param>
    /// <param name="tupleCustomTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="start">The zero-based first logical element.</param>
    /// <param name="count">The maximum count, or zero for every remaining element.</param>
    /// <param name="origin">The physical storage of the array owner.</param>
    /// <param name="lifetime">The containing snapshot's optional retirement state.</param>
    /// <returns>The requested array element page.</returns>
    List<DebugVariableInfo> ExpandArray(
        nint array,
        string? parentEvaluateName,
        int? frameId,
        DebugStopGeneration generation,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        int start,
        int count,
        ManagedValueOrigin? origin = null,
        ManagedResultsViewLifetime? lifetime = null);
}
