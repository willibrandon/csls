using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized direct managed-value assignment.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Assigns one immediate variable-container child without executing target code.
    /// </summary>
    [McpServerTool(
        Name = "debug_variable_set",
        Title = "Set a .NET debugger variable",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugAssignmentResult))]
    [Description("Set one child of a current-generation variable container using a side-effect-free value expression. Requires an active debug_agent_control_set grant and the exact stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetVariableAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive parent handle returned by debug_scopes_get or debug_variables_get.")]
        int variablesReference,
        [Description("Immediate variable, field, or array-element name in the parent container.")]
        string name,
        [Description("Side-effect-free value expression in the selected frame language.")]
        string value,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetVariableAsync(
            debugSession,
            stopGeneration,
            variablesReference,
            name,
            value,
            cancellationToken));

    /// <summary>
    /// Assigns one writable source expression without executing target code.
    /// </summary>
    [McpServerTool(
        Name = "debug_expression_set",
        Title = "Set a .NET debugger expression",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugAssignmentResult))]
    [Description("Set a writable local, argument, instance field, or array element using a side-effect-free value expression. Requires an active debug_agent_control_set grant and the exact stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetExpressionAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        [Description("Writable local, argument, instance-field, or array-element expression.")]
        string expression,
        [Description("Side-effect-free value expression in the selected frame language.")]
        string value,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetExpressionAsync(
            debugSession,
            stopGeneration,
            frameId,
            expression,
            value,
            cancellationToken));
}
