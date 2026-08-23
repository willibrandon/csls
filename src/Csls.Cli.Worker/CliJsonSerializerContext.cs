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
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(IReadOnlyList<ControlSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(IReadOnlyList<TextEdit>))]
[JsonSerializable(typeof(IReadOnlyList<Location>))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
internal sealed partial class CliJsonSerializerContext : JsonSerializerContext;
