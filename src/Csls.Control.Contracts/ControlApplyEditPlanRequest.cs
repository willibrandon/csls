namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one previously previewed edit plan for explicit application.
/// </summary>
public sealed record ControlApplyEditPlanRequest
{
    /// <summary>
    /// Gets the unguessable one-use plan identifier.
    /// </summary>
    public required Guid PlanId { get; init; }
}
