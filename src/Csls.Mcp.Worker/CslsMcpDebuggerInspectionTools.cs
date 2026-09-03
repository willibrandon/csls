using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-aware debugger thread and stack inspection.
/// </summary>
[McpServerToolType]
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates inspection tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerInspectionTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Gets managed threads for one exact stop generation.
    /// </summary>
    [McpServerTool(
        Name = "debug_threads_get",
        Title = "Get .NET debugger threads",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugThreadsResult))]
    [Description("Get managed threads for one explicit debugger session and current stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetThreadsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() =>
            _broker.GetThreadsAsync(debugSession, stopGeneration, cancellationToken));

    /// <summary>
    /// Gets a bounded managed stack page for one thread and stop generation.
    /// </summary>
    [McpServerTool(
        Name = "debug_stack_get",
        Title = "Get .NET debugger stack",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugStackResult))]
    [Description("Get a bounded managed stack page for one thread in the current stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetStackAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        [Description("Positive managed thread identifier.")]
        int threadId,
        CancellationToken cancellationToken,
        [Description("Zero-based first frame.")]
        int startFrame = 0,
        [Description("Maximum frames from 0 through 256; zero requests all remaining within the engine bound.")]
        int levels = 0) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetStackAsync(
            debugSession,
            stopGeneration,
            threadId,
            startFrame,
            levels,
            cancellationToken));
}
