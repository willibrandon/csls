using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized debugger expression execution.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Gets debugger-presented variables whose proxies or enumeration may execute target code.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="variablesReference">The generation-bound variable container.</param>
    /// <param name="cancellationToken">Cancels target-code presentation and variable expansion.</param>
    /// <param name="start">The zero-based first variable to return.</param>
    /// <param name="count">The maximum number of variables to return.</param>
    /// <returns>The presented variables and replacement stopped generation.</returns>
    [McpServerTool(
        Name = "debug_variables_get_presented",
        Title = "Get debugger-presented .NET variables",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugVariablesResult))]
    [Description("Get debugger-presented child variables, including DebuggerTypeProxy and Results View. Requires an active debug_agent_control_set grant and the exact stopGeneration because constructors, getters, and enumerable expansion may execute arbitrary target code. Listing a Results View row does not enumerate it. Resolving that lazy row returns one non-lazy snapshot variable; use its variablesReference and the replacement stopGeneration with debug_variables_get or a variables resource to read pages without further execution.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetPresentedVariablesAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound variable container identifier.")]
        int variablesReference,
        CancellationToken cancellationToken,
        [Description("Zero-based first variable.")]
        int start = 0,
        [Description("Maximum variables from 0 through 256; zero requests all remaining within the engine bound.")]
        int count = 0) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetPresentedVariablesAsync(
            debugSession,
            stopGeneration,
            variablesReference,
            start,
            count,
            cancellationToken));

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
    [Description("Execute an explicitly qualified instance or loaded-type static method in the target. Requires an active debug_agent_control_set grant and the exact stopGeneration; the method may have arbitrary side effects.")]
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
