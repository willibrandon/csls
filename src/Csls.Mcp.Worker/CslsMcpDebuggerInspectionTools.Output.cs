using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded cursor-addressable target output.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets one target-output page after a stable sequence cursor.
    /// </summary>
    [McpServerTool(Name = "debug_output_get", Title = "Get .NET target output",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugOutputResult))]
    [Description("Get a bounded retained stdout/stderr page after a stable session-local sequence.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetOutputAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        CancellationToken cancellationToken,
        [Description("Last observed sequence, or zero for the retained beginning.")]
        long afterSequence = 0,
        [Description("Maximum entries from 1 through 256.")]
        int count = 128) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetOutputAsync(
            debugSession,
            afterSequence,
            count,
            cancellationToken));
}
