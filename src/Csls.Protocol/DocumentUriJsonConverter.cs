using System.Text.Json;
using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Converts document URI values to and from their LSP string representation.
/// </summary>
public sealed class DocumentUriJsonConverter : JsonConverter<DocumentUri>
{
    /// <inheritdoc />
    public override DocumentUri Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        string value = reader.GetString()
            ?? throw new JsonException("Document URIs cannot be null.");
        return DocumentUri.Parse(value);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        DocumentUri value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
