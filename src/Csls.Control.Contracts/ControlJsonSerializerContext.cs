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
[JsonSerializable(typeof(ControlSessionInfo))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
public sealed partial class ControlJsonSerializerContext : JsonSerializerContext;
