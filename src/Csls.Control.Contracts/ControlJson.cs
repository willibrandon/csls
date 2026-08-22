using System.Text.Json;

namespace Csls.Control.Contracts;

/// <summary>
/// Creates source-generated serializer options shared by control and MCP boundaries.
/// </summary>
public static class ControlJson
{
    /// <summary>
    /// Creates mutable serializer options backed by generated control-contract metadata.
    /// </summary>
    /// <returns>Serializer options configured for versioned control payloads.</returns>
    public static JsonSerializerOptions CreateSerializerOptions() =>
        new(ControlJsonSerializerContext.Default.Options)
        {
            TypeInfoResolver = ControlJsonSerializerContext.Default
        };
}
