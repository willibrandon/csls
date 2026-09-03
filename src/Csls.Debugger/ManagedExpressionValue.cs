using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries one runtime-backed or computed value through safe expression binding.
/// </summary>
/// <param name="Display">The debugger-facing value description.</param>
/// <param name="Scalar">The decoded primitive value when one is available.</param>
/// <param name="HasScalar">Whether <paramref name="Scalar"/> is authoritative.</param>
internal sealed record ManagedExpressionValue(
    DebugVariableInfo Display,
    object? Scalar,
    bool HasScalar)
{
    /// <summary>
    /// Converts the internal value to the protocol-neutral evaluation result.
    /// </summary>
    /// <returns>The debugger-facing evaluation result.</returns>
    internal DebugEvaluateResult ToResult() => new(
        Display.Value,
        Display.Type,
        Display.VariablesReference,
        Display.MemoryReference);
}
