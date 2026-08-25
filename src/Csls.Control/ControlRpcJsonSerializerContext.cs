using StreamJsonRpc.Protocol;
using System.Text.Json.Serialization;

namespace Csls.Control;

/// <summary>
/// Provides generated JSON metadata for StreamJsonRpc error payloads on the control transport.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CommonErrorData))]
[JsonSerializable(typeof(ControlRpcCancellationRequest))]
internal sealed partial class ControlRpcJsonSerializerContext : JsonSerializerContext;
