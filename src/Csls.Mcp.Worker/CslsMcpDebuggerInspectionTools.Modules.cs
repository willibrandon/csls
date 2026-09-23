using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded managed module inspection.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets a bounded managed module page for one debugger session.
    /// </summary>
    [McpServerTool(
        Name = "debug_modules_get",
        Title = "Get .NET debugger modules",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugModulesResult))]
    [Description("Get a bounded page of managed modules and validated symbol status for one explicit debugger session.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetModulesAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        CancellationToken cancellationToken,
        [Description("Zero-based first module.")]
        int startModule = 0,
        [Description("Maximum modules from 0 through 256; zero requests all remaining within the engine bound.")]
        int moduleCount = 0) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GetModulesAsync(
            debugSession,
            startModule,
            moduleCount,
            cancellationToken));
}
