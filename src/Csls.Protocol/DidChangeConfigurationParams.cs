using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Contains configuration settings pushed by an LSP client.
/// </summary>
public sealed record DidChangeConfigurationParams
{
    /// <summary>
    /// Gets the client-defined configuration payload.
    /// </summary>
    public JsonElement Settings { get; init; }
}
