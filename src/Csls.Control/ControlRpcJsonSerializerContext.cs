using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;

namespace Csls.Control;

/// <summary>
/// Provides generated JSON metadata for StreamJsonRpc error payloads on the control transport.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CommonErrorData))]
internal sealed partial class ControlRpcJsonSerializerContext : JsonSerializerContext;
