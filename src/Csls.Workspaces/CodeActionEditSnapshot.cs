using Csls.Protocol;

namespace Csls.Workspaces;

/// <summary>
/// Binds one concrete code action to its optional immutable edit snapshot.
/// </summary>
public sealed record CodeActionEditSnapshot
{
    /// <summary>
    /// Gets the editor-visible code action.
    /// </summary>
    public required CodeAction Action { get; init; }

    /// <summary>
    /// Gets the edit snapshot when the action changes source documents.
    /// </summary>
    public WorkspaceEditSnapshot? EditSnapshot { get; init; }
}
