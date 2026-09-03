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
[JsonSerializable(typeof(McpDebugThreadsResult))]
[JsonSerializable(typeof(McpDebugStackResult))]
[JsonSerializable(typeof(McpDebugScopesResult))]
[JsonSerializable(typeof(McpDebugVariablesResult))]
[JsonSerializable(typeof(McpDebugModulesResult))]
[JsonSerializable(typeof(McpDebugSourceBreakpoint))]
[JsonSerializable(typeof(McpDebugFunctionBreakpoint))]
[JsonSerializable(typeof(McpDebugInstructionBreakpoint))]
[JsonSerializable(typeof(McpDebugExceptionBreakpoint))]
[JsonSerializable(typeof(McpDebugSourceBreakpointsResult))]
[JsonSerializable(typeof(McpDebugFunctionBreakpointsResult))]
[JsonSerializable(typeof(McpDebugInstructionBreakpointsResult))]
[JsonSerializable(typeof(McpDebugExceptionBreakpointsResult))]
[JsonSerializable(typeof(McpDebugExceptionResult))]
[JsonSerializable(typeof(McpDebugMemoryResult))]
[JsonSerializable(typeof(McpDebugDisassemblyResult))]
[JsonSerializable(typeof(McpDebugStepTargetsResult))]
[JsonSerializable(typeof(McpDebugGotoTargetsResult))]
[JsonSerializable(typeof(McpDebugSourceResult))]
[JsonSerializable(typeof(McpDebuggerError))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugSourceBreakpoint>))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugFunctionBreakpoint>))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugInstructionBreakpoint>))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugExceptionBreakpoint>))]
[JsonSerializable(typeof(IReadOnlyList<McpDebugSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string?>))]
[JsonSerializable(typeof(CallToolResult))]
internal sealed partial class McpJsonSerializerContext : JsonSerializerContext;
