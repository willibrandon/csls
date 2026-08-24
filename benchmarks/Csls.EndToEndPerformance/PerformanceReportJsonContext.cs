using System.Text.Json.Serialization;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Provides source-generated JSON metadata for end-to-end performance reports.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PerformanceReport))]
internal sealed partial class PerformanceReportJsonContext : JsonSerializerContext;
