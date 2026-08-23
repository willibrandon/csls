using System.Text.Json.Serialization;
using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Provides generated System.Text.Json metadata for the versioned control protocol.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlHoverRequest))]
[JsonSerializable(typeof(ControlHoverResult))]
[JsonSerializable(typeof(ControlDiagnosticRequest))]
[JsonSerializable(typeof(ControlCompletionRequest))]
[JsonSerializable(typeof(ControlSessionInfo))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionItemKind))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(IReadOnlyList<TextEdit>))]
public sealed partial class ControlJsonSerializerContext : JsonSerializerContext;
