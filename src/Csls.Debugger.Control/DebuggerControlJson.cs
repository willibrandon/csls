using StreamJsonRpc;
using System.Text.Json;

namespace Csls.Debugger.Control;

/// <summary>
/// Creates source-generated JSON options for debugger control RPC.
/// </summary>
internal static class DebuggerControlJson
{
    /// <summary>
    /// Creates one mutable serializer configuration.
    /// </summary>
    /// <returns>The debugger control serializer options.</returns>
    internal static JsonSerializerOptions CreateOptions() =>
        new(DebuggerControlJsonSerializerContext.Default.Options)
        {
            TypeInfoResolver = DebuggerControlJsonSerializerContext.Default
        };

    /// <summary>
    /// Creates a formatter constrained to the source-generated debugger contract metadata.
    /// </summary>
    /// <returns>A formatter owned by the caller.</returns>
    internal static SystemTextJsonFormatter CreateFormatter() =>
        new()
        {
            JsonSerializerOptions = CreateOptions()
        };
}
