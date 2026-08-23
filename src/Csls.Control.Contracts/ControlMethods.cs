namespace Csls.Control.Contracts;

/// <summary>
/// Defines the versioned StreamJsonRpc method names for the csls control protocol.
/// </summary>
public static class ControlMethods
{
    /// <summary>
    /// Gets the method that returns the current language-server session.
    /// </summary>
    public const string GetSession = "csls/control/v1/session/get";

    /// <summary>
    /// Gets the method that resolves hover information in the current workspace snapshot.
    /// </summary>
    public const string GetHover = "csls/control/v1/hover/get";

    /// <summary>
    /// Gets the method that returns compiler and analyzer diagnostics for one document.
    /// </summary>
    public const string GetDiagnostics = "csls/control/v1/diagnostics/get";

    /// <summary>
    /// Gets the method that returns bounded completion candidates for one document position.
    /// </summary>
    public const string GetCompletion = "csls/control/v1/completion/get";

    /// <summary>
    /// Gets the method that returns source definitions for one document position.
    /// </summary>
    public const string GetDefinition = "csls/control/v1/definition/get";

    /// <summary>
    /// Gets the method that returns source declarations for one document position.
    /// </summary>
    public const string GetDeclaration = "csls/control/v1/declaration/get";

    /// <summary>
    /// Gets the method that returns source type definitions for one document position.
    /// </summary>
    public const string GetTypeDefinition = "csls/control/v1/type-definition/get";

    /// <summary>
    /// Gets the method that returns source implementations for one document position.
    /// </summary>
    public const string GetImplementation = "csls/control/v1/implementation/get";

    /// <summary>
    /// Gets the method that returns syntax selection hierarchies for document positions.
    /// </summary>
    public const string GetSelectionRanges = "csls/control/v1/selection-ranges/get";

    /// <summary>
    /// Gets the method that returns semantic symbol highlights within one document.
    /// </summary>
    public const string GetDocumentHighlights = "csls/control/v1/document-highlights/get";

    /// <summary>
    /// Gets the method that returns source references for one document position.
    /// </summary>
    public const string GetReferences = "csls/control/v1/references/get";

    /// <summary>
    /// Gets the method that returns hierarchical declarations for one document.
    /// </summary>
    public const string GetDocumentSymbols = "csls/control/v1/document-symbols/get";

    /// <summary>
    /// Gets the method that searches declarations across the current workspace.
    /// </summary>
    public const string GetWorkspaceSymbols = "csls/control/v1/workspace-symbols/get";

    /// <summary>
    /// Gets the method that resolves an exact workspace symbol source range.
    /// </summary>
    public const string ResolveWorkspaceSymbol = "csls/control/v1/workspace-symbols/resolve";

    /// <summary>
    /// Gets the method that returns overload-aware signature help.
    /// </summary>
    public const string GetSignatureHelp = "csls/control/v1/signature-help/get";

    /// <summary>
    /// Gets the method that previews a version-aware semantic rename edit.
    /// </summary>
    public const string PreviewRename = "csls/control/v1/rename/preview";

    /// <summary>
    /// Gets the method that previews complete-document formatting edits.
    /// </summary>
    public const string PreviewFormatting = "csls/control/v1/formatting/preview";

    /// <summary>
    /// Gets the method that previews concrete Roslyn code actions.
    /// </summary>
    public const string GetCodeActions = "csls/control/v1/code-actions/get";

    /// <summary>
    /// Gets the method that explicitly applies one previously previewed edit plan.
    /// </summary>
    public const string ApplyEditPlan = "csls/control/v1/edit-plans/apply";
}
