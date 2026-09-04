using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-aware debugger scope and variable inspection.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets scopes for one current-generation managed frame.
    /// </summary>
    [McpServerTool(
        Name = "debug_scopes_get",
        Title = "Get .NET debugger scopes",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugScopesResult))]
    [Description("Get arguments, locals, and other scopes for one frame in the current stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetScopesAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetScopesAsync(
            debugSession,
            stopGeneration,
            frameId,
            cancellationToken));

    /// <summary>
    /// Gets a bounded variable page from one current-generation scope or value.
    /// </summary>
    [McpServerTool(
        Name = "debug_variables_get",
        Title = "Get .NET debugger variables",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugVariablesResult))]
    [Description("Get a bounded child page from one variable container in the current stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetVariablesAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound variable container identifier.")]
        int variablesReference,
        CancellationToken cancellationToken,
        [Description("Zero-based first variable.")]
        int start = 0,
        [Description("Maximum variables from 0 through 256; zero requests all remaining within the engine bound.")]
        int count = 0) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetVariablesAsync(
            debugSession,
            stopGeneration,
            variablesReference,
            start,
            count,
            cancellationToken));

    /// <summary>
    /// Evaluates a side-effect-free expression in one current-generation frame.
    /// </summary>
    [McpServerTool(
        Name = "debug_evaluate",
        Title = "Evaluate a .NET debugger expression",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugEvaluationResult))]
    [Description("Evaluate a source-language-aware side-effect-free expression without executing target code.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> EvaluateAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        [Description("Side-effect-free source expression to evaluate.")]
        string expression,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.EvaluateAsync(
            debugSession,
            stopGeneration,
            frameId,
            expression,
            cancellationToken));

    /// <summary>
    /// Evaluates an ordered set of independent side-effect-free watch expressions.
    /// </summary>
    [McpServerTool(
        Name = "debug_watches_get",
        Title = "Get .NET debugger watches",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugWatchesResult))]
    [Description("Evaluate one through 64 independent side-effect-free watches without executing target code.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetWatchesAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Current positive stop generation.")]
        long stopGeneration,
        [Description("Positive generation-bound frame identifier.")]
        int frameId,
        [Description("One through 64 source expressions, each no longer than 4096 UTF-16 code units.")]
        IReadOnlyList<string> expressions,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetWatchesAsync(
            debugSession,
            stopGeneration,
            frameId,
            expressions,
            cancellationToken));
}
