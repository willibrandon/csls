using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads exceptions, target memory, and managed-IL for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumMemoryReadBytes = 65_536;
    private const int MaximumDisassemblyInstructions = 256;

    /// <summary>
    /// Gets the managed exception responsible for an exact stop.
    /// </summary>
    internal Task<McpDebugExceptionResult> GetExceptionAsync(
        string debugSession,
        long stopGeneration,
        int threadId,
        CancellationToken cancellationToken)
    {
        ValidatePositive(threadId, nameof(threadId));
        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugExceptionResult(
                session.Id,
                stopGeneration,
                await client.GetExceptionInfoAsync(
                    new DebugExceptionInfoRequest(threadId),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Reads a bounded target-memory range at an exact stop.
    /// </summary>
    internal Task<McpDebugMemoryResult> ReadMemoryAsync(
        string debugSession,
        long stopGeneration,
        string memoryReference,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueReference(memoryReference, nameof(memoryReference));
        if (count is <= 0 or > MaximumMemoryReadBytes)
        {
            throw InvalidRequest(
                $"count must be between 1 and {MaximumMemoryReadBytes} bytes.");
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) =>
            {
                DebugMemoryReadResult result = await client.ReadMemoryAsync(
                    new DebugMemoryReadRequest(memoryReference, offset, count),
                    token).ConfigureAwait(false);
                return new McpDebugMemoryResult(
                    session.Id,
                    stopGeneration,
                    result.Address,
                    result.Data,
                    result.UnreadableBytes);
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets bounded managed-IL disassembly at an exact stop.
    /// </summary>
    internal Task<McpDebugDisassemblyResult> DisassembleAsync(
        string debugSession,
        long stopGeneration,
        DebugDisassemblyRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueReference(request.InstructionReference, "instructionReference");
        if (request.InstructionCount is <= 0 or > MaximumDisassemblyInstructions)
        {
            throw InvalidRequest(
                $"instructionCount must be between 1 and {MaximumDisassemblyInstructions}.");
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugDisassemblyResult(
                session.Id,
                stopGeneration,
                (await client.DisassembleAsync(request, token).ConfigureAwait(false))
                    .Instructions),
            cancellationToken);
    }

    private static void ValidateOpaqueReference(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw InvalidRequest($"{name} must contain between 1 and 512 characters.");
        }
    }
}
