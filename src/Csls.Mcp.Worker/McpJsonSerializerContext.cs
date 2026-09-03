using ModelContextProtocol.Protocol;
using System.Text.Json.Serialization;

namespace Csls.Mcp.Worker;

/// <summary>
/// Provides generated JSON metadata for MCP-specific structured results.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(McpWorkspaceSummary))]
[JsonSerializable(typeof(McpDebugSessionInfo))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string?>))]
[JsonSerializable(typeof(CallToolResult))]
internal sealed partial class McpJsonSerializerContext : JsonSerializerContext;
