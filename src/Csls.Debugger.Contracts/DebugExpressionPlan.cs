namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries one versioned language-neutral debugger expression plan.
/// </summary>
/// <param name="Version">The expression-plan contract version.</param>
/// <param name="Language">The source language that bound the expression.</param>
/// <param name="Root">The immutable root operation.</param>
public sealed record DebugExpressionPlan(
    int Version,
    DebugExpressionLanguage Language,
    DebugExpressionNode Root);
