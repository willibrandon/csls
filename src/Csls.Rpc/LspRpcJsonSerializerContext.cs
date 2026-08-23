using StreamJsonRpc.Protocol;
using System.Text.Json.Serialization;

namespace Csls.Rpc;

/// <summary>
/// Provides generated JSON metadata for StreamJsonRpc error payloads on the LSP transport.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CommonErrorData))]
internal sealed partial class LspRpcJsonSerializerContext : JsonSerializerContext;
