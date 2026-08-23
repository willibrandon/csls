using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Exposes one concrete code action and its optional one-use application plan.
/// </summary>
public sealed record ControlCodeActionPlan
{
    /// <summary>
    /// Gets the editor-visible code action.
    /// </summary>
    public required CodeAction Action { get; init; }

    /// <summary>
    /// Gets the one-use application plan when the action changes source documents.
    /// </summary>
    public ControlEditPlan? EditPlan { get; init; }
}
