namespace Csls.Debugger;

/// <summary>
/// Carries a bound property receiver and exact accessor until supervised evaluation begins.
/// </summary>
/// <param name="Receiver">The receiver retained in the current stop generation.</param>
/// <param name="DeclaringType">The exact constructed type that declares the accessor.</param>
/// <param name="Getter">The metadata-backed property accessor.</param>
internal sealed record ManagedPropertyEvaluation(
    ManagedExpressionValue Receiver,
    ManagedBoundType DeclaringType,
    ManagedPropertyGetter Getter);
