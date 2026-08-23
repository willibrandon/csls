namespace Csls.Protocol;

/// <summary>
/// Identifies one virtual C# document whose source text is requested.
/// </summary>
public sealed class CSharpMetadataParams
{
    /// <summary>
    /// Gets the virtual C# document identifier.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }
}
