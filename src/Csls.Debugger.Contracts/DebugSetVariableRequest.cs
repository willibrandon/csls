namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one variable-container child for an exact stopped-generation assignment.
/// </summary>
/// <param name="StopGeneration">The exact stopped generation authorizing the write.</param>
/// <param name="VariablesReference">The generation-bound parent container.</param>
/// <param name="Name">The immediate child name.</param>
/// <param name="Value">The source-language value expression to assign.</param>
public sealed record DebugSetVariableRequest(
    long StopGeneration,
    int VariablesReference,
    string Name,
    string Value);
