namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one expression result from a stopped managed frame.
/// </summary>
/// <param name="Result">The language-neutral value display.</param>
/// <param name="Type">The language-neutral runtime type display.</param>
/// <param name="VariablesReference">The child-container handle, or zero when not expandable.</param>
/// <param name="MemoryReference">The opaque stopped-state memory handle, or null when unavailable.</param>
/// <param name="TargetCodeExecuted">Whether evaluation ran target code and invalidated stopped-state handles.</param>
/// <param name="NamedVariables">The known number of named children, or null when unavailable.</param>
/// <param name="IndexedVariables">The known number of indexed children, or null when unavailable.</param>
public sealed record DebugEvaluateResult(
    string Result,
    string Type,
    int VariablesReference,
    string? MemoryReference,
    bool TargetCodeExecuted = false,
    int? NamedVariables = null,
    int? IndexedVariables = null);
