using Csls.Debugger.Contracts;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-aware exception and navigation-target inspection.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets the exception responsible for the current stop.
    /// </summary>
    [McpServerTool(Name = "debug_exception_get", Title = "Get .NET debug exception",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugExceptionResult))]
    [Description("Get the managed exception responsible for one thread's exact stopped generation.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetExceptionAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive managed thread identifier.")]
        int threadId,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetExceptionAsync(
            debugSession,
            stopGeneration,
            threadId,
            cancellationToken));

    /// <summary>
    /// Gets source-aware Step Into targets for one frame.
    /// </summary>
    [McpServerTool(Name = "debug_step_targets_get", Title = "Get .NET step targets",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugStepTargetsResult))]
    [Description("Get source-aware Step Into calls for one frame in the exact stopped generation.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetStepTargetsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetStepTargetsAsync(
            debugSession,
            stopGeneration,
            frameId,
            cancellationToken));

    /// <summary>
    /// Gets runtime-approved instruction-pointer destinations.
    /// </summary>
    [McpServerTool(Name = "debug_goto_targets_get", Title = "Get .NET go-to targets",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugGotoTargetsResult))]
    [Description("Get runtime-approved go-to destinations for one source position in the exact stopped generation.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetGotoTargetsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        [Description("Absolute source document path.")]
        string sourcePath,
        [Description("Positive one-based source line.")]
        int line,
        CancellationToken cancellationToken,
        [Description("Optional positive one-based source column.")]
        int? column = null) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetGotoTargetsAsync(
            debugSession,
            stopGeneration,
            new DebugGotoTargetsRequest(frameId, sourcePath, line, column),
            cancellationToken));
}
