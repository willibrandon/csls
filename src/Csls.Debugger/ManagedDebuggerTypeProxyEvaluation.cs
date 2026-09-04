namespace Csls.Debugger;

/// <summary>
/// Preserves one original object while its debugger type proxy executes target code.
/// </summary>
/// <param name="EvaluateName">The original source expression when one is available.</param>
/// <param name="ThreadId">The managed thread selected for proxy construction.</param>
internal sealed record ManagedDebuggerTypeProxyEvaluation(
    string? EvaluateName,
    int ThreadId);
