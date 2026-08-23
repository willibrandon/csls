namespace Csls.Protocol;

/// <summary>
/// Advertises csls extensions that expose virtual C# source documents.
/// </summary>
public sealed class CSharpExperimentalServerCapabilities
{
    /// <summary>
    /// Gets whether navigation can return URIs resolved through csharp/metadata.
    /// </summary>
    public bool MetadataUris { get; init; }
}
