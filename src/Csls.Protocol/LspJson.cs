using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Creates the shared source-generated JSON configuration used by LSP transports.
/// </summary>
public static class LspJson
{
    /// <summary>
    /// Creates mutable serializer options backed by the generated LSP metadata.
    /// </summary>
    /// <returns>Serializer options configured for LSP payloads.</returns>
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(LspJsonSerializerContext.Default.Options)
        {
            TypeInfoResolver = LspJsonSerializerContext.Default
        };
        return options;
    }
}
