using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries one function-evaluation result and its retained runtime value.
/// </summary>
/// <param name="Result">The debugger-facing evaluation result.</param>
/// <param name="RuntimeValueReference">The internal generation-owned runtime value handle.</param>
/// <param name="Generation">The stop generation produced by target execution.</param>
internal sealed record ManagedFunctionEvaluationResult(
    DebugEvaluateResult Result,
    int RuntimeValueReference,
    DebugStopGeneration Generation);
