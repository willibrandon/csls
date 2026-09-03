using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized debugger execution control.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpDebuggerExecutionTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates execution tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerExecutionTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Pauses, continues, or steps one debugger session with explicit control permission.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="operation">The pause, continue, or step operation.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="stopGeneration">The current generation required by continue and step.</param>
    /// <param name="threadId">The managed thread required by step.</param>
    /// <param name="stepKind">The into, over, or out source step kind.</param>
    /// <returns>The debugger-session state after accepting the operation.</returns>
    [McpServerTool(
        Name = "debug_execution_control",
        Title = "Control .NET debugger execution",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Pause, continue, or source-step one explicit debugger session. Requires its agentControl grant; continue and step also require the current stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> ControlAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Execution operation: pause, continue, or step.")]
        string operation,
        CancellationToken cancellationToken,
        [Description("Current positive stop generation required by continue and step.")]
        long? stopGeneration = null,
        [Description("Positive managed thread identifier required by step.")]
        int? threadId = null,
        [Description("Source step kind required by step: into, over, or out.")]
        string? stepKind = null) =>
        McpDebuggerToolResult.RunAsync(() => _broker.ExecuteAsync(
            debugSession,
            operation,
            stopGeneration,
            threadId,
            stepKind,
            cancellationToken));
}
