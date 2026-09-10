namespace Csls.Debugger;

/// <summary>
/// Identifies the expansion and memory handles retained for one managed value.
/// </summary>
/// <param name="VariablesReference">The expandable-container handle, or zero.</param>
/// <param name="MemoryReference">The opaque memory handle, or null.</param>
internal readonly record struct ManagedValueReferences(
    int VariablesReference,
    string? MemoryReference);
