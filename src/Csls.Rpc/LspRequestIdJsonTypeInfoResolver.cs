using StreamJsonRpc;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Csls.Rpc;

/// <summary>
/// Supplies cancellation identifier metadata for the converter registered by StreamJsonRpc.
/// </summary>
internal sealed class LspRequestIdJsonTypeInfoResolver : IJsonTypeInfoResolver
{
    /// <summary>
    /// Gets the shared stateless resolver for LSP cancellation identifiers.
    /// </summary>
    internal static LspRequestIdJsonTypeInfoResolver Instance { get; } = new();

    /// <summary>
    /// Creates request identifier metadata without reflecting over the upstream internal converter.
    /// </summary>
    /// <param name="type">The requested serialization type.</param>
    /// <param name="options">The formatter options containing its request identifier converter.</param>
    /// <returns>Metadata for a request identifier, or null for other types.</returns>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        if (type != typeof(RequestId))
        {
            return null;
        }

        JsonConverter<RequestId> converter = options.Converters
            .OfType<JsonConverter<RequestId>>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The LSP formatter must register its request identifier converter.");
        return JsonMetadataServices.CreateValueInfo<RequestId>(options, converter);
    }
}
