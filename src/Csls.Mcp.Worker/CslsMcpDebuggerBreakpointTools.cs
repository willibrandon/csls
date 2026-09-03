using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes deterministic source and managed-function breakpoint replacement.
/// </summary>
[McpServerToolType]
internal sealed partial class CslsMcpDebuggerBreakpointTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates breakpoint tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerBreakpointTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Replaces every source breakpoint for one document.
    /// </summary>
    [McpServerTool(
        Name = "debug_source_breakpoints_set",
        Title = "Set .NET source breakpoints",
        Destructive = true,
        Idempotent = true,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSourceBreakpointsResult))]
    [Description("Replace all source breakpoints for one absolute document at an exact stopped generation. Requires agentControl.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetSourceBreakpointsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Absolute source document path.")]
        string sourcePath,
        [Description("Complete replacement list; an empty list clears the document.")]
        IReadOnlyList<McpDebugSourceBreakpoint> breakpoints,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetSourceBreakpointsAsync(
            debugSession,
            stopGeneration,
            sourcePath,
            breakpoints,
            cancellationToken));

    /// <summary>
    /// Replaces every managed-function breakpoint.
    /// </summary>
    [McpServerTool(
        Name = "debug_function_breakpoints_set",
        Title = "Set .NET function breakpoints",
        Destructive = true,
        Idempotent = true,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugFunctionBreakpointsResult))]
    [Description("Replace all managed-function breakpoints at an exact stopped generation. Requires agentControl.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetFunctionBreakpointsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Complete replacement list; an empty list clears every function breakpoint.")]
        IReadOnlyList<McpDebugFunctionBreakpoint> breakpoints,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetFunctionBreakpointsAsync(
            debugSession,
            stopGeneration,
            breakpoints,
            cancellationToken));
}
