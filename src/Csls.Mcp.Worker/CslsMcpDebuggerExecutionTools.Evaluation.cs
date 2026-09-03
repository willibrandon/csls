using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized debugger expression execution.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Executes target code for an expression in one current-generation managed frame.
    /// </summary>
    [McpServerTool(
        Name = "debug_execute_expression",
        Title = "Execute a .NET debugger expression",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugEvaluationResult))]
    [Description("Execute an explicitly qualified instance or loaded-type static method in the target. Requires agentControl and the exact stopGeneration; the method may have arbitrary side effects.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> ExecuteExpressionAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        [Description("Explicitly qualified instance or loaded-type static call with supported argument values.")]
        string expression,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.ExecuteExpressionAsync(
            debugSession,
            stopGeneration,
            frameId,
            expression,
            cancellationToken));
}
