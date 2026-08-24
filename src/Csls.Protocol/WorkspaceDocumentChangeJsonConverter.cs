using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Csls.Protocol;

/// <summary>
/// Converts the mixed LSP workspace document-change array without reflection metadata.
/// </summary>
public sealed class WorkspaceDocumentChangeJsonConverter : JsonConverter<WorkspaceDocumentChange>
{
    /// <inheritdoc />
    public override WorkspaceDocumentChange Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var document = JsonDocument.ParseValue(ref reader);
        JsonElement value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Workspace document changes must be JSON objects.");
        }

        if (!value.TryGetProperty("kind", out JsonElement kind))
        {
            return value.Deserialize(GetTypeInfo<TextDocumentEdit>(options))
                ?? throw new JsonException("The text document edit was null.");
        }

        if (kind.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Workspace resource-operation kinds must be strings.");
        }

        WorkspaceDocumentChange? change = kind.GetString() switch
        {
            "create" => value.Deserialize(GetTypeInfo<CreateFile>(options)),
            "rename" => value.Deserialize(GetTypeInfo<RenameFile>(options)),
            "delete" => value.Deserialize(GetTypeInfo<DeleteFile>(options)),
            _ => throw new JsonException($"Unsupported workspace resource operation '{kind}'.")
        };
        return change ?? throw new JsonException("The workspace resource operation was null.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        WorkspaceDocumentChange value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        switch (value)
        {
            case TextDocumentEdit documentEdit:
                JsonSerializer.Serialize(
                    writer,
                    documentEdit,
                    GetTypeInfo<TextDocumentEdit>(options));
                break;
            case CreateFile createFile:
                JsonSerializer.Serialize(
                    writer,
                    createFile,
                    GetTypeInfo<CreateFile>(options));
                break;
            case RenameFile renameFile:
                JsonSerializer.Serialize(
                    writer,
                    renameFile,
                    GetTypeInfo<RenameFile>(options));
                break;
            case DeleteFile deleteFile:
                JsonSerializer.Serialize(
                    writer,
                    deleteFile,
                    GetTypeInfo<DeleteFile>(options));
                break;
            default:
                throw new JsonException(
                    $"Unsupported workspace document change '{value.GetType().Name}'.");
        }
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions options) =>
        options.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new JsonException($"No JSON metadata is registered for {typeof(T).Name}.");
}
