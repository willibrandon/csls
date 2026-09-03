namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one debugger variable and its immediate formatted value.
/// </summary>
/// <param name="Name">The source or synthetic variable name.</param>
/// <param name="Value">The language-neutral value display.</param>
/// <param name="Type">The language-neutral runtime type display.</param>
/// <param name="VariablesReference">The child-container handle, or zero when not expandable.</param>
/// <param name="MemoryReference">The opaque stopped-state memory handle, or null when unavailable.</param>
public sealed record DebugVariableInfo(
    string Name,
    string Value,
    string Type,
    int VariablesReference,
    string? MemoryReference);
