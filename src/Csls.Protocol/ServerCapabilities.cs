namespace Csls.Protocol;

/// <summary>
/// Advertises only the LSP capabilities implemented by the running server.
/// </summary>
public sealed record ServerCapabilities
{
    /// <summary>
    /// Gets the negotiated position encoding.
    /// </summary>
    public string PositionEncoding { get; init; } = "utf-16";

    /// <summary>
    /// Gets the text document synchronization behavior.
    /// </summary>
    public required TextDocumentSyncOptions TextDocumentSync { get; init; }

    /// <summary>
    /// Gets whether hover requests are supported.
    /// </summary>
    public bool HoverProvider { get; init; }

    /// <summary>
    /// Gets the pull-diagnostic provider behavior.
    /// </summary>
    public DiagnosticOptions? DiagnosticProvider { get; init; }

    /// <summary>
    /// Gets the completion provider behavior.
    /// </summary>
    public CompletionOptions? CompletionProvider { get; init; }

    /// <summary>
    /// Gets whether source definition requests are supported.
    /// </summary>
    public bool DefinitionProvider { get; init; }

    /// <summary>
    /// Gets whether source declaration requests are supported.
    /// </summary>
    public bool DeclarationProvider { get; init; }

    /// <summary>
    /// Gets whether type-definition requests are supported.
    /// </summary>
    public bool TypeDefinitionProvider { get; init; }

    /// <summary>
    /// Gets whether implementation requests are supported.
    /// </summary>
    public bool ImplementationProvider { get; init; }

    /// <summary>
    /// Gets whether syntax-aware selection range requests are supported.
    /// </summary>
    public bool SelectionRangeProvider { get; init; }

    /// <summary>
    /// Gets whether semantic document highlight requests are supported.
    /// </summary>
    public bool DocumentHighlightProvider { get; init; }

    /// <summary>
    /// Gets the complete-document semantic-token behavior and token legend.
    /// </summary>
    public SemanticTokensOptions? SemanticTokensProvider { get; init; }

    /// <summary>
    /// Gets whether source reference requests are supported.
    /// </summary>
    public bool ReferencesProvider { get; init; }

    /// <summary>
    /// Gets whether hierarchical document symbol requests are supported.
    /// </summary>
    public bool DocumentSymbolProvider { get; init; }

    /// <summary>
    /// Gets the workspace symbol search and resolve behavior.
    /// </summary>
    public WorkspaceSymbolOptions? WorkspaceSymbolProvider { get; init; }

    /// <summary>
    /// Gets the signature help trigger behavior.
    /// </summary>
    public SignatureHelpOptions? SignatureHelpProvider { get; init; }

    /// <summary>
    /// Gets the rename preparation and edit behavior.
    /// </summary>
    public RenameOptions? RenameProvider { get; init; }

    /// <summary>
    /// Gets whether complete-document formatting is supported.
    /// </summary>
    public bool DocumentFormattingProvider { get; init; }

    /// <summary>
    /// Gets the supported code-action categories.
    /// </summary>
    public CodeActionOptions? CodeActionProvider { get; init; }
}
