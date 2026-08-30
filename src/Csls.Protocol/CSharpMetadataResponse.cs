namespace Csls.Protocol;

/// <summary>
/// Describes source text for one generated or metadata-backed C# document.
/// </summary>
public sealed class CSharpMetadataResponse
{
    /// <summary>
    /// Gets the readable source document name presented by clients.
    /// </summary>
    public string? DocumentName { get; init; }

    /// <summary>
    /// Gets the project that owns the virtual document.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets the assembly containing the represented symbol or generated document.
    /// </summary>
    public string? AssemblyName { get; init; }

    /// <summary>
    /// Gets the represented symbol or generator hint name.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Gets the complete C# source text displayed by the client.
    /// </summary>
    public required string Source { get; init; }
}
