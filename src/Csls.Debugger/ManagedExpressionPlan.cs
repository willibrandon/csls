namespace Csls.Debugger;

/// <summary>
/// Carries a validated versioned plan for side-effect-free managed evaluation.
/// </summary>
/// <param name="Version">The expression-plan contract version.</param>
/// <param name="RootName">The local, argument, or instance root name.</param>
/// <param name="Segments">The ordered member and array accesses.</param>
internal sealed record ManagedExpressionPlan(
    int Version,
    string RootName,
    IReadOnlyList<ManagedExpressionSegment> Segments);
