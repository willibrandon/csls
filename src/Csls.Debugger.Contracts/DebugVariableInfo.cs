namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one debugger variable and its immediate formatted value.
/// </summary>
/// <param name="Name">The source or synthetic variable name.</param>
/// <param name="Value">The language-neutral value display.</param>
/// <param name="Type">The language-neutral runtime type display.</param>
/// <param name="VariablesReference">The child-container handle, or zero when not expandable.</param>
/// <param name="MemoryReference">The opaque stopped-state memory handle, or null when unavailable.</param>
/// <param name="EvaluateName">The source expression that retrieves the value, or null when unavailable.</param>
/// <param name="PresentationKind">The client presentation category for the variable.</param>
/// <param name="NamedVariables">The known number of named children, or null when unavailable.</param>
/// <param name="IndexedVariables">The known number of indexed children, or null when unavailable.</param>
/// <param name="IsIndexed">Whether this entry is an indexed child rather than a named member.</param>
public sealed record DebugVariableInfo(
    string Name,
    string Value,
    string Type,
    int VariablesReference,
    string? MemoryReference,
    string? EvaluateName,
    DebugVariablePresentationKind PresentationKind = DebugVariablePresentationKind.Normal,
    int? NamedVariables = null,
    int? IndexedVariables = null,
    bool IsIndexed = false);
