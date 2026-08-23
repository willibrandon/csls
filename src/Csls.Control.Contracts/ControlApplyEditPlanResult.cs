namespace Csls.Control.Contracts;

/// <summary>
/// Reports the new workspace generation and documents changed by an applied edit plan.
/// </summary>
public sealed record ControlApplyEditPlanResult
{
    /// <summary>
    /// Gets the workspace generation published after application.
    /// </summary>
    public required long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the absolute document paths changed by the plan.
    /// </summary>
    public required IReadOnlyList<string> DocumentPaths { get; init; }
}
