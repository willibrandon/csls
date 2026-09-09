namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded page from one current-generation variable container.
/// </summary>
/// <param name="VariablesReference">The generation-bound container handle.</param>
/// <param name="Start">The zero-based first variable.</param>
/// <param name="Count">The maximum count, or zero for all remaining variables.</param>
/// <param name="AllowTargetCodeExecution">Whether target-code presentation is authorized.</param>
/// <param name="Filter">The child category to select before applying pagination.</param>
public sealed record DebugVariablesRequest(
    int VariablesReference,
    int Start,
    int Count,
    bool AllowTargetCodeExecution,
    DebugVariableFilter Filter = DebugVariableFilter.All)
{
    /// <summary>
    /// Gets bounded live-value progress delivered synchronously on the engine actor and marshaled over private RPC.
    /// </summary>
    public IProgress<DebugValueReadProgress>? Progress { get; init; }

    /// <summary>
    /// Gets the optional request-scoped captured-memory progress receiver for dump inspection.
    /// </summary>
    public IProgress<DebugDumpReadProgress>? DumpReadProgress { get; init; }
}
