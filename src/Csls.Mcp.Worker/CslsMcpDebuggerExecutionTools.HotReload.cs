using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized compiler-driven managed Hot Reload.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Applies one compiler-produced module generation to a stopped managed target.
    /// </summary>
    [McpServerTool(
        Name = "debug_hot_reload",
        Title = "Apply .NET Hot Reload deltas",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugHotReloadResult))]
    [Description("Apply compiler-produced metadata, IL, and Portable PDB deltas to one Hot Reload-enabled module. Requires an active debug_agent_control_set grant and exact stop and module generations.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> ApplyHotReloadAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive module identifier returned by debug_modules_get.")]
        int moduleId,
        [Description("Exact non-negative hotReloadGeneration returned for that module.")]
        int expectedModuleGeneration,
        [Description("Base64 compiler-produced ECMA-335 metadata delta.")]
        string metadataDeltaBase64,
        [Description("Base64 compiler-produced managed IL delta.")]
        string ilDeltaBase64,
        [Description("Base64 compiler-produced minimal Portable PDB delta.")]
        string pdbDeltaBase64,
        [Description("Compiler-produced aggregate type-definition tokens changed by this generation.")]
        IReadOnlyList<int> updatedTypes,
        [Description("Compiler capability names required by this generation; compare with the module's hotReloadCapabilities.")]
        IReadOnlyList<string> requiredCapabilities,
        [Description("Compiler-produced aggregate method-definition tokens changed by this generation.")]
        IReadOnlyList<int> updatedMethods,
        [Description("Compiler-produced active old-instruction to updated source-span mappings; pass an empty array when no updated method is active.")]
        IReadOnlyList<McpDebugHotReloadActiveStatement> activeStatements,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.ApplyHotReloadAsync(
            debugSession,
            stopGeneration,
            moduleId,
            expectedModuleGeneration,
            metadataDeltaBase64,
            ilDeltaBase64,
            pdbDeltaBase64,
            updatedTypes,
            requiredCapabilities,
            updatedMethods,
            activeStatements,
            cancellationToken));
}
