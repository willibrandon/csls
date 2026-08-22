using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Provides generated System.Text.Json metadata for every registered LSP contract.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DocumentUri))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(InitializedParams))]
[JsonSerializable(typeof(MarkupContent))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(Range))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(TextDocumentIdentifier))]
[JsonSerializable(typeof(TextDocumentItem))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(TextDocumentSyncOptions))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceFolder>))]
public sealed partial class LspJsonSerializerContext : JsonSerializerContext;
