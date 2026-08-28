using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Describes one dynamically registered LSP capability.
/// </summary>
public sealed record Registration
{
    /// <summary>
    /// Gets the stable registration identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the registered LSP method name.
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Gets the method-specific registration options.
    /// </summary>
    public JsonElement? RegisterOptions { get; init; }
}
