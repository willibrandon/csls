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
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ControlHoverResult))]
[JsonSerializable(typeof(ControlDocumentPrecondition))]
[JsonSerializable(typeof(ControlEditPlan))]
[JsonSerializable(typeof(ControlApplyEditPlanResult))]
[JsonSerializable(typeof(ControlCodeActionPlan))]
[JsonSerializable(typeof(ControlSessionInfo))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionItemKind))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CodeAction))]
[JsonSerializable(typeof(CodeActionContext))]
[JsonSerializable(typeof(CodeActionParams))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(DocumentSymbol))]
[JsonSerializable(typeof(DocumentHighlight))]
[JsonSerializable(typeof(DocumentHighlightKind))]
[JsonSerializable(typeof(FormattingOptions))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(SelectionRange))]
[JsonSerializable(typeof(SignatureInformation))]
[JsonSerializable(typeof(ParameterInformation))]
[JsonSerializable(typeof(SymbolKind))]
[JsonSerializable(typeof(WorkspaceSymbol))]
[JsonSerializable(typeof(WorkspaceSymbolData))]
[JsonSerializable(typeof(WorkspaceSymbolLocation))]
[JsonSerializable(typeof(WorkspaceEdit))]
[JsonSerializable(typeof(TextDocumentEdit))]
[JsonSerializable(typeof(OptionalVersionedTextDocumentIdentifier))]
[JsonSerializable(typeof(IReadOnlyList<ControlSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(IReadOnlyList<TextEdit>))]
[JsonSerializable(typeof(IReadOnlyList<Location>))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<DocumentHighlight>))]
[JsonSerializable(typeof(IReadOnlyList<SelectionRange>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceSymbol>))]
[JsonSerializable(typeof(IReadOnlyList<SignatureInformation>))]
[JsonSerializable(typeof(IReadOnlyList<ParameterInformation>))]
[JsonSerializable(typeof(IReadOnlyList<CodeAction>))]
[JsonSerializable(typeof(IReadOnlyList<TextDocumentEdit>))]
[JsonSerializable(typeof(IReadOnlyList<ControlDocumentPrecondition>))]
[JsonSerializable(typeof(IReadOnlyList<ControlCodeActionPlan>))]
internal sealed partial class CliJsonSerializerContext : JsonSerializerContext;
