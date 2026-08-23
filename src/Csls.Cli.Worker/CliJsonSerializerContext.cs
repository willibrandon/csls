using System.Text.Json.Serialization;
using Csls.Control.Contracts;
using Csls.Protocol;

namespace Csls.Cli.Worker;

/// <summary>
/// Provides generated System.Text.Json metadata for versioned CLI response envelopes.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CliError))]
[JsonSerializable(typeof(CliResponseEnvelope))]
[JsonSerializable(typeof(ControlHoverResult))]
[JsonSerializable(typeof(ControlSessionInfo))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionItemKind))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(DocumentSymbol))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(SignatureInformation))]
[JsonSerializable(typeof(ParameterInformation))]
[JsonSerializable(typeof(SymbolKind))]
[JsonSerializable(typeof(WorkspaceSymbol))]
[JsonSerializable(typeof(WorkspaceSymbolData))]
[JsonSerializable(typeof(WorkspaceSymbolLocation))]
[JsonSerializable(typeof(IReadOnlyList<ControlSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(IReadOnlyList<TextEdit>))]
[JsonSerializable(typeof(IReadOnlyList<Location>))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<SignatureInformation>))]
[JsonSerializable(typeof(IReadOnlyList<ParameterInformation>))]
internal sealed partial class CliJsonSerializerContext : JsonSerializerContext;
