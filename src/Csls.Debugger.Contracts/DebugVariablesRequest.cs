namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded page from one current-generation variable container.
/// </summary>
/// <param name="VariablesReference">The generation-bound container handle.</param>
/// <param name="Start">The zero-based first variable.</param>
/// <param name="Count">The maximum count, or zero for all remaining variables.</param>
public sealed record DebugVariablesRequest(int VariablesReference, int Start, int Count);
