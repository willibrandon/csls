namespace Csls.Protocol;

/// <summary>
/// Contains the document opened by the client.
/// </summary>
public sealed record DidOpenTextDocumentParams
{
    /// <summary>
    /// Gets the opened document and its contents.
    /// </summary>
    public required TextDocumentItem TextDocument { get; init; }
}
