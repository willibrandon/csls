using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Preserves a validated string expression's declaration across guarded target allocation.
/// </summary>
/// <param name="Plan">The literal string plan executed by the runtime evaluator.</param>
/// <param name="DeclaredType">The source expression's exact type before materialization.</param>
internal sealed record ManagedStringAssignmentPlan(DebugExpressionPlan Plan, ManagedBoundType DeclaredType);
