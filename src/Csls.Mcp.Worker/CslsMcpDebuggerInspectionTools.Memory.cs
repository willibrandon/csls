using Csls.Debugger.Contracts;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded generation-aware memory and managed-IL inspection.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Reads a bounded range relative to an opaque target-memory reference.
    /// </summary>
    [McpServerTool(Name = "debug_memory_read", Title = "Read .NET target memory",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugMemoryResult))]
    [Description("Read up to 65536 target bytes from a generation-bound memory reference.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> ReadMemoryAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Opaque memory reference returned by a variable.")]
        string memoryReference,
        [Description("Requested byte count from 1 through 65536.")]
        int count,
        CancellationToken cancellationToken,
        [Description("Signed byte offset relative to the reference.")]
        long offset = 0) =>
        McpDebuggerToolResult.RunAsync(() => _broker.ReadMemoryAsync(
            debugSession,
            stopGeneration,
            memoryReference,
            offset,
            count,
            cancellationToken));

    /// <summary>
    /// Reads a bounded exact-count managed-IL instruction range.
    /// </summary>
    [McpServerTool(Name = "debug_disassemble", Title = "Disassemble .NET managed IL",
        Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true,
        UseStructuredContent = true, OutputSchemaType = typeof(McpDebugDisassemblyResult))]
    [Description("Disassemble up to 256 managed-IL instructions from a generation-bound location.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> DisassembleAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Opaque managed-IL reference returned by a stack frame or instruction.")]
        string instructionReference,
        [Description("Exact instruction count from 1 through 256.")]
        int instructionCount,
        CancellationToken cancellationToken,
        [Description("Signed byte offset applied before instruction selection.")]
        long byteOffset = 0,
        [Description("Signed instruction offset applied after the byte offset.")]
        long instructionOffset = 0,
        [Description("Include symbolic metadata operand names.")]
        bool resolveSymbols = true) =>
        McpDebuggerToolResult.RunAsync(() => _broker.DisassembleAsync(
            debugSession,
            stopGeneration,
            new DebugDisassemblyRequest(
                instructionReference,
                byteOffset,
                instructionOffset,
                instructionCount,
                resolveSymbols),
            cancellationToken));
}
