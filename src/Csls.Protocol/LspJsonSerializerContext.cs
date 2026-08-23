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
[JsonSerializable(typeof(CompletionContext))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionItemKind))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionOptions))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionTriggerKind))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticOptions))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(DocumentUri))]
[JsonSerializable(typeof(DocumentDiagnosticParams))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(InitializedParams))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(MarkupContent))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(Range))]
[JsonSerializable(typeof(ReferenceContext))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(TextDocumentIdentifier))]
[JsonSerializable(typeof(TextDocumentContentChangeEvent))]
[JsonSerializable(typeof(TextDocumentItem))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(TextDocumentSyncOptions))]
[JsonSerializable(typeof(TextEdit))]
[JsonSerializable(typeof(VersionedTextDocumentIdentifier))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<TextEdit>))]
[JsonSerializable(typeof(IReadOnlyList<Location>))]
[JsonSerializable(typeof(IReadOnlyList<TextDocumentContentChangeEvent>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceFolder>))]
public sealed partial class LspJsonSerializerContext : JsonSerializerContext;
