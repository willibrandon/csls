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
    /// Gets whether source reference requests are supported.
    /// </summary>
    public bool ReferencesProvider { get; init; }
}
