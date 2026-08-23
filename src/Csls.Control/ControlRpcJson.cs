using System.Text.Json;
using Csls.Control.Contracts;

namespace Csls.Control;

/// <summary>
/// Creates control serializer options that include application and transport payload metadata.
/// </summary>
internal static class ControlRpcJson
{
    /// <summary>
    /// Creates mutable source-generated options for one control StreamJsonRpc connection.
    /// </summary>
    /// <returns>The complete control transport serializer options.</returns>
    internal static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = ControlJson.CreateSerializerOptions();
        options.TypeInfoResolverChain.Add(ControlRpcJsonSerializerContext.Default);
        return options;
    }
}
