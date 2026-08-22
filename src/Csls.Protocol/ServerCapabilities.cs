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
}
