using Csls.Protocol;
using System.Text.Json;

namespace Csls.Rpc;

/// <summary>
/// Creates LSP serializer options that include application and transport payload metadata.
/// </summary>
public static class LspRpcJson
{
    /// <summary>
    /// Creates mutable source-generated options for one LSP StreamJsonRpc connection.
    /// </summary>
    /// <returns>The complete LSP transport serializer options.</returns>
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = LspJson.CreateSerializerOptions();
        Configure(options);
        return options;
    }

    /// <summary>
    /// Adds StreamJsonRpc transport payload metadata to existing LSP serializer options.
    /// </summary>
    /// <param name="options">The mutable LSP serializer options.</param>
    internal static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TypeInfoResolverChain.Contains(LspRpcJsonSerializerContext.Default))
        {
            options.TypeInfoResolverChain.Add(LspRpcJsonSerializerContext.Default);
        }
    }
}
