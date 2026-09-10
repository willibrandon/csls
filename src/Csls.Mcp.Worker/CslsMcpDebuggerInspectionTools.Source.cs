using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded generation-aware debugger source retrieval.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets one bounded character page from a debugger source reference.
    /// </summary>
    [McpServerTool(Name = "debug_source_get", Title = "Get .NET debugger source",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugSourceResult))]
    [Description("Get a bounded source-text page from one generation-bound source reference.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetSourceAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive source reference returned by a stack frame.")]
        int sourceReference,
        CancellationToken cancellationToken,
        [Description("Zero-based character offset.")]
        int start = 0,
        [Description("Character count from 1 through 65536.")]
        int count = 16384) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetSourceAsync(
            debugSession,
            stopGeneration,
            sourceReference,
            start,
            count,
            cancellationToken));
}
