namespace Csls.Debugger.Contracts;

/// <summary>
/// Reports live value inspection and the stopped session's value registry sizes.
/// </summary>
/// <param name="VariablesReference">The requested variable-container handle.</param>
/// <param name="FormattedValues">The runtime values formatted, including nested display values.</param>
/// <param name="PageValues">The values selected for a completed page, or zero before completion.</param>
/// <param name="RetainedValues">The value bindings currently owned by the stopped-session registry.</param>
/// <param name="RetainedMemoryReferences">The memory references currently owned by the stopped-session registry.</param>
/// <param name="State">Whether inspection is active or has completed, canceled, or failed.</param>
public sealed record DebugValueReadProgress(
    int VariablesReference,
    int FormattedValues,
    int PageValues,
    int RetainedValues,
    int RetainedMemoryReferences,
    DebugValueReadState State);
